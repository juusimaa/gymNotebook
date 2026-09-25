param location string
param appName string
param managedEnvironmentId string
param containerImage string = 'mcr.microsoft.com/azuredocs/containerapps-helloworld:latest'
param apiUrl string
param tags object

// Optional custom domain (e.g. gymnotebook.fit). Empty means "Azure FQDN only",
// which keeps the module deployable into a fresh environment.
//
// The managed certificate is deliberately NOT created here: Azure only issues it
// after the hostname's DNS records validate and the hostname is already on the
// app, so it is created once by hand (`az containerapp hostname add` + `bind`)
// and referenced by name afterwards. What Bicep must own is the *binding*:
// every deployment replaces the whole ingress block, so a binding that is not
// declared here is silently removed on the next deploy.
param customDomainName string = ''
param customDomainCertificateName string = ''

var hasCustomDomain = !empty(customDomainName)

var placeholderImage = 'mcr.microsoft.com/azuredocs/containerapps-helloworld:latest'
var isPlaceholder = containerImage == placeholderImage
var effectivePort = isPlaceholder ? 80 : 80

resource managedEnvironment 'Microsoft.App/managedEnvironments@2026-01-01' existing = {
  name: last(split(managedEnvironmentId, '/'))
}

resource managedCertificate 'Microsoft.App/managedEnvironments/managedCertificates@2026-01-01' existing = if (hasCustomDomain) {
  parent: managedEnvironment
  name: customDomainCertificateName
}

resource containerApp 'Microsoft.App/containerApps@2026-01-01' = {
  name: appName
  location: location
  tags: tags
  properties: {
    managedEnvironmentId: managedEnvironmentId
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: effectivePort
        transport: 'auto'
        allowInsecure: false
        customDomains: hasCustomDomain
          ? [
              {
                name: customDomainName
                certificateId: managedCertificate.id
                bindingType: 'SniEnabled'
              }
            ]
          : []
        traffic: [
          {
            latestRevision: true
            weight: 100
          }
        ]
      }
      // GHCR packages are public, so no registry credentials or registry identity are used.
      registries: []
      secrets: []
    }
    template: {
      containers: [
        {
          name: 'frontend'
          image: containerImage
          resources: {
            cpu: '0.25'
            memory: '0.5Gi'
          }
          env: [
            {
              name: 'API_URL'
              value: apiUrl
            }
          ]
        }
      ]
      scale: {
        minReplicas: 0
        maxReplicas: 1
      }
    }
  }
}

output fqdn string = containerApp.properties.configuration.ingress.fqdn
