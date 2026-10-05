using 'main.bicep'

// Fill these in from your Entra External ID tenant (see api/README.md, "Set up Entra External ID").
param authAuthority = 'https://<tenant-subdomain>.ciamlogin.com/<external-tenant-id>/v2.0'
param apiClientId = '<api-app-client-id>'
param externalTenantId = '<external-tenant-id>'
param graphClientId = ''          // set once the Graph app is set up; empty = skip Entra-side delete/revoke
param allowedOrigins = []         // e.g. ['https://slideshow.example.com', 'http://localhost:5173']
param useSqlFreeLimit = true
