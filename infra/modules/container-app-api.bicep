param location string
param appName string
param managedEnvironmentId string
param containerImage string = 'mcr.microsoft.com/azuredocs/containerapps-helloworld:latest'
param corsOrigins string
@secure()
param neonConnectionString string
@secure()
param jwtSecret string
@secure()
param inviteCode string
param tags object

var placeholderImage = 'mcr.microsoft.com/azuredocs/containerapps-helloworld:latest'
var isPlaceholder = containerImage == placeholderImage
var effectivePort = isPlaceholder ? 80 : 8080

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
        traffic: [
          {
            latestRevision: true
            weight: 100
          }
        ]
      }
      // GHCR packages are public, so no registry credentials or registry identity are used.
      registries: []
      // Secure deployment parameters become Container Apps secrets only in Phase 2.
      secrets: isPlaceholder ? [] : [
        {
          name: 'connection-strings-default'
          value: neonConnectionString
        }
        {
          name: 'jwt-secret'
          value: jwtSecret
        }
        {
          name: 'invite-code'
          value: inviteCode
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'api'
          image: containerImage
          resources: {
            cpu: '0.5'
            memory: '1Gi'
          }
          probes: isPlaceholder ? [] : [
            {
              type: 'Liveness'
              httpGet: {
                path: '/health'
                port: 8080
                scheme: 'HTTP'
              }
              initialDelaySeconds: 5
              periodSeconds: 10
            }
            {
              type: 'Readiness'
              httpGet: {
                path: '/health'
                port: 8080
                scheme: 'HTTP'
              }
              initialDelaySeconds: 5
              periodSeconds: 10
            }
          ]
          env: isPlaceholder ? [] : concat(
            [
              {
                name: 'Jwt__ExpiryMinutes'
                value: '30'
              }
              {
                name: 'CORS_ORIGINS'
                value: corsOrigins
              }
              {
                // Privacy/account lifecycle feature switch (specs/001, P25). Plain value,
                // not a secret. Stays 'false' until every release gate has evidence; the
                // flip to 'true' is its own reviewed PR (tasks.md T084).
                name: 'PRIVACY_LIFECYCLE_ENABLED'
                value: 'false'
              }
            ],
            [
              {
                name: 'ConnectionStrings__Default'
                secretRef: 'connection-strings-default'
              }
              {
                name: 'Jwt__Secret'
                secretRef: 'jwt-secret'
              }
              {
                name: 'INVITE_CODE'
                secretRef: 'invite-code'
              }
            ]
          )
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
