using 'main.bicep'

// Entra External ID tenant "Slideshow" (slideshowmap.onmicrosoft.com). None of these are secrets.
param authAuthority = 'https://slideshowmap.ciamlogin.com/ddba6b11-5443-454a-916d-3a9237c12525/v2.0'
param apiClientId = '107ba552-513b-4b86-ba0f-e8e1ca1c7afd'   // "Slideshow API" app registration
param externalTenantId = 'ddba6b11-5443-454a-916d-3a9237c12525'
param graphClientId = ''          // set once the Graph app is set up; empty = skip Entra-side delete/revoke

// The website (Static Web App) is always allowed; add more origins here if needed.
param allowedOrigins = []
param webLocation = 'eastus2'      // Static Web Apps region (not offered in eastus)
param sqlLocation = 'centralus'    // new subscriptions can't create SQL servers in eastus/eastus2 (checked October 2026)
param useSqlFreeLimit = true
