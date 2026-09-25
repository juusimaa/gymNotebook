targetScope = 'resourceGroup'

@minLength(1)
@maxLength(64)
param environmentName string

@minLength(1)
param location string

param sessionId string
param deployedBy string
param createdAt string

// Phase 1 uses the public placeholder. Phase 2 must provide immutable GHCR SHA tags.
param apiContainerImage string = 'mcr.microsoft.com/azuredocs/containerapps-helloworld:latest'
param frontendContainerImage string = 'mcr.microsoft.com/azuredocs/containerapps-helloworld:latest'

// The API cannot derive this value in the same deployment that derives the frontend's
// API_URL, because that would make the two Container Apps depend on one another.
// Phase 2 supplies the frontend's HTTPS origin after Phase 1 reveals its FQDN.
param corsOrigins string = 'https://localhost.invalid'

// The frontend's public hostname and the environment's managed certificate for
// it. See container-app-frontend.bicep for why the certificate is referenced,
// not created. Leave both empty to deploy without a custom domain.
param frontendCustomDomain string = ''
param frontendCustomDomainCertificateName string = ''

@secure()
param neonConnectionString string

@secure()
param jwtSecret string

@secure()
param inviteCode string

var tags = {
  'app-onboard-skill': 'true'
  'app-onboard-session-id': sessionId
  'created-at': createdAt
  environment: environmentName
  'deployed-by': deployedBy
}

module logAnalytics './modules/log-analytics.bicep' = {
  name: 'log-analytics'
  params: {
    location: location
    workspaceName: 'log-gymnote-prod-58dd'
    tags: tags
  }
}

module managedEnvironment './modules/container-apps-environment.bicep' = {
  name: 'container-apps-environment'
  dependsOn: [
    logAnalytics
  ]
  params: {
    location: location
    environmentName: 'cae-gymnote-prod-58dd'
    workspaceName: 'log-gymnote-prod-58dd'
    tags: tags
  }
}

module api './modules/container-app-api.bicep' = {
  name: 'container-app-api'
  params: {
    location: location
    appName: 'ca-gymnote-prod-58dd-api'
    managedEnvironmentId: managedEnvironment.outputs.id
    containerImage: apiContainerImage
    corsOrigins: corsOrigins
    neonConnectionString: neonConnectionString
    jwtSecret: jwtSecret
    inviteCode: inviteCode
    tags: tags
  }
}

module frontend './modules/container-app-frontend.bicep' = {
  name: 'container-app-frontend'
  params: {
    location: location
    appName: 'ca-gymnote-prod-58dd-web'
    managedEnvironmentId: managedEnvironment.outputs.id
    containerImage: frontendContainerImage
    apiUrl: 'https://${api.outputs.fqdn}'
    customDomainName: frontendCustomDomain
    customDomainCertificateName: frontendCustomDomainCertificateName
    tags: tags
  }
}

output apiFqdn string = api.outputs.fqdn
output frontendFqdn string = frontend.outputs.fqdn
