param location string
param workspaceName string
param tags object

// Identifying operational logs may be kept for at most 30 days (FR-018,
// contracts/operations.md → Retention schedule). The workspace default below
// covers most tables, but Azure gives these built-in tables their own 90-day
// default, so each one is pinned explicitly (research R7, T069).
//
// The list is hard-coded: a table Microsoft adds later arrives at 90 days and
// is not covered until it is added here. Removing a name does not revert that
// table either (deployments are incremental). The retention runbook's live
// query ("any table not at 30/30?") is the real check, not this list.
//
// AzureActivity and Usage also default to 90 days but are deliberately left
// out: Azure rejects anything below 90 for them ("minimum allowed value 90
// days", seen on the 2026-09-26 deploy). That is acceptable because neither
// holds app-user data: Usage is billing metadata (volume per table), and
// AzureActivity is empty. Nothing may send the Activity Log here without a
// review first, since it would then be kept 90 days (research R7 → T069).
var tablesWith90DayDefault = [
  'AppAvailabilityResults'
  'AppBrowserTimings'
  'AppDependencies'
  'AppEvents'
  'AppExceptions'
  'AppGenAIContent'
  'AppMetrics'
  'AppPageViews'
  'AppPerformanceCounters'
  'AppRequests'
  'AppSystemEvents'
  'AppTraces'
]

resource workspace 'Microsoft.OperationalInsights/workspaces@2026-03-01' = {
  name: workspaceName
  location: location
  tags: tags
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
    features: {
      // Without this flag, 30-day retention can keep data for about 31 days,
      // which exceeds the 30-day maximum. It only applies while retention is
      // exactly 30 days. Microsoft documents it at workspace level, so whether
      // it also covers the per-table overrides below is verified after deploy.
      immediatePurgeDataOn30Days: true
      // Stated explicitly so that setting `features` cannot reset the current
      // live value (who may read logs through resource-level permissions).
      enableLogAccessUsingOnlyResourcePermissions: true
    }
  }
}

// For a built-in table, a PUT with only the retention properties is enough;
// the schema and protection level stay Azure's (what-if lists them as
// "Delete" only because the template does not repeat them). The plan is
// stated so a deploy can never switch a table away from Analytics.
// totalRetentionInDays equal to retentionInDays means no long-term (archive)
// retention after the interactive period.
resource tableRetention 'Microsoft.OperationalInsights/workspaces/tables@2026-03-01' = [
  for tableName in tablesWith90DayDefault: {
    parent: workspace
    name: tableName
    properties: {
      plan: 'Analytics'
      retentionInDays: 30
      totalRetentionInDays: 30
    }
  }
]

output id string = workspace.id
