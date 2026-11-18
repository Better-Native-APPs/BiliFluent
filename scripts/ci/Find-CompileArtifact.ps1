[CmdletBinding()]
param([switch]$Publish)
# Only successful same-repository, same-workflow, exact-head-SHA runs may supply reusable bytes.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'CompileArtifact.ps1')
$repo = $env:GITHUB_REPOSITORY
$repoId = [long]$env:GITHUB_REPOSITORY_ID
$sha = $env:GITHUB_SHA
$found = $null
$reason = 'not-found'
if ($env:GITHUB_EVENT_NAME -ceq 'push' -and ($env:GITHUB_REF -ceq 'refs/heads/main' -or $env:GITHUB_REF -like 'refs/tags/v*')) {
    try {
        $workflow = Invoke-CiGitHubApi "/repos/$repo/actions/workflows/release.yml"
        if ($workflow.path -cne '.github/workflows/release.yml') { throw 'Unexpected workflow path.' }
        $page = 1
        do {
            $runs = Invoke-CiGitHubApi "/repos/$repo/actions/workflows/$($workflow.id)/runs?head_sha=$sha&status=success&per_page=100&page=$page"
            foreach ($run in @($runs.workflow_runs | Sort-Object id -Descending)) {
                if (-not (Test-CiReusableRun $run $repoId $workflow.id $sha)) { continue }
                $artifacts = @()
                $artifactPage = 1
                do {
                    $batch = Invoke-CiGitHubApi "/repos/$repo/actions/runs/$($run.id)/artifacts?per_page=100&page=$artifactPage"
                    $artifacts += @($batch.artifacts)
                    $artifactPage++
                } while ($artifacts.Count -lt $batch.total_count -and @($batch.artifacts).Count -gt 0)
                $found = Select-CiCompileArtifact -Run $run -Artifacts $artifacts -RepositoryId $repoId -WorkflowId $workflow.id -SourceSha $sha
                if ($found) { break }
            }
            $page++
        } while (-not $found -and ($page - 1) * 100 -lt $runs.total_count -and @($runs.workflow_runs).Count -gt 0)
        if ($found) { $reason = 'same-sha-success' }
    } catch {
        Write-Warning 'Prior workflow artifacts could not be queried; selecting a full compilation.'
        $found = $null
        $reason = 'api-unavailable'
    }
}
$plan = Get-CiCompilePlan -Event $env:GITHUB_EVENT_NAME -Ref $env:GITHUB_REF -Publish $Publish.IsPresent -HasArtifact ([bool]$found)
Set-CiOutput 'plan' $plan
Set-CiOutput 'reason' $reason
if ($found) {
    Set-CiOutput 'artifact_id' ([string]$found.id)
    Set-CiOutput 'archive_sha256' $found.digest.Substring('sha256:'.Length)
    Set-CiOutput 'run_id' ([string]$found.workflow_run.id)
}
