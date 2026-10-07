param location string
param appName string
param managedEnvironmentId string
param containerImage string = 'mcr.microsoft.com/azuredocs/containerapps-helloworld:latest'
param corsOrigins string
@secure()
param neonConnectionString string
@secure()
param jwtSecret string
// Cloudflare Turnstile's secret (specs/002 plan D9). The site key is public and goes to
// the frontend app instead; the two are created together on the Turnstile widget.
@secure()
param turnstileSecretKey string
@secure()
param resendApiKey string
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
          name: 'turnstile-secret-key'
          value: turnstileSecretKey
        }
        {
          name: 'resend-api-key'
          value: resendApiKey
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
              {
                // Take the client address from the ingress's X-Forwarded-For entry so the
                // per-IP login/register limit is per visitor, not one shared bucket for the
                // ingress (Program.cs, UseForwardedHeaders). Safe here because external
                // ingress is the only route into the container.
                name: 'FORWARDED_HEADERS_ENABLED'
                value: 'true'
              }
              {
                // Outgoing email through Resend (specs/002 plan D7). The API refuses to
                // start in Production with any other backend, or without the key, sender
                // and app URL below. Links in emails point at the frontend's own domain.
                name: 'Email__Backend'
                value: 'resend'
              }
              {
                name: 'EMAIL_FROM'
                value: 'Gym Notebook <no-reply@mail.gymnotebook.fit>'
              }
              {
                name: 'APP_URL'
                value: 'https://gymnotebook.fit'
              }
              {
                // Where a Turnstile token may have been solved: the frontend's own
                // domain only. The API refuses to start with the secret but no hostnames.
                name: 'TURNSTILE_HOSTNAMES'
                value: 'gymnotebook.fit'
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
                name: 'TURNSTILE_SECRET_KEY'
                secretRef: 'turnstile-secret-key'
              }
              {
                name: 'RESEND_API_KEY'
                secretRef: 'resend-api-key'
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
