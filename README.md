# build-common
Common build targets and scripts necessary for libraries and components 

## What's included

1. `Common.props` file necessary to be included from your root `Directory.Build.props`
2. Shared `.target` files in `targets/` directory. Includes NullableEnable, TrimmingEnable, SignEnable, PackEnable and more.
   
    These targets can be referenced with `$(RepositoryTargetsRoot)` prefix

    ```xml
    <Import Project="$(RepositoryTargetsRoot)/NullableEnable.targets" />
    ```

3. Nuke extension, including BuildToLocalCache and simple obfuscation/ilmerge wrappers
4. Avalonia strong name key and public key
5. Some branding images, like default nuget package icon
6. Reusable GitHub Actions workflows under `.github/workflows/`:
    - `library-cicd.yml` — build, test, pack, push, and optionally GitHub-release a library.
    - `source-release.yml` — package a customer-facing source zip when a library is released. See `scripts/source-release/stage.sh`.

## Source-release workflow

Drop the following into `.github/workflows/source-release.yml` in any consumer
repository to wire up source-zip packaging on a `release/*` branch push. A small
`setup` job derives the version from the branch name and passes it to the reusable
workflow, so the source zip ships in the same release as everything else:

```yaml
name: Source Release

on:
    push:
        branches: [ "release/*" ]
    release:
        types: [published]
    workflow_dispatch:
        inputs:
            ref:
                description: 'Commitish (branch, tag, or SHA) to package. Leave empty to use the dispatched branch.'
                required: false
                default: ''
            version:
                description: 'Version for the zip (no leading v).'
                required: true
                default: 0.0.0-test1
            upload:
                description: 'Upload the zip to Release Manager.'
                required: false
                type: boolean
                default: false

concurrency:
    group: source-release-${{ github.event.release.tag_name || inputs.version || github.ref }}
    cancel-in-progress: false

jobs:
    # Derive the version (and whether to upload) from whichever trigger fired. A
    # release/* branch push and a published release always upload.
    setup:
        runs-on: ubuntu-latest
        outputs:
            version: ${{ steps.v.outputs.version }}
            upload: ${{ steps.v.outputs.upload }}
        steps:
            - id: v
              run: |
                  set -euo pipefail
                  case "${{ github.event_name }}" in
                      push)    ver="${GITHUB_REF#refs/heads/release/}"; upload=true ;;
                      release) ver="${{ github.event.release.tag_name }}"; upload=true ;;
                      *)       ver="${{ inputs.version }}"; upload="${{ inputs.upload }}" ;;
                  esac
                  ver="${ver#v}"
                  echo "version=$ver" >> "$GITHUB_OUTPUT"
                  echo "upload=$upload" >> "$GITHUB_OUTPUT"

    source-release:
        needs: setup
        # Pin to a specific commit SHA (not a branch or tag) — any move of
        # @main would otherwise immediately affect every consumer. Look up
        # the latest source-release SHA from this repo's commit history and
        # bump intentionally. Dependabot's `github-actions` ecosystem can
        # automate this.
        uses: AvaloniaUI/build-common/.github/workflows/source-release.yml@<sha>
        with:
            project_name: Avalonia.Controls.Example
            ref: ${{ inputs.ref }}                               # empty outside workflow_dispatch
            version: ${{ needs.setup.outputs.version }}
            upload: ${{ needs.setup.outputs.upload == 'true' }}
            release_manager_base_url: ${{ vars.RELEASE_MANAGER_BASE_URL }}
            release_manager_product: ${{ vars.RELEASE_MANAGER_PRODUCT_NAME }}
            # allow_list: .github/source-release/projects.txt    # default
            # solution_file: Avalonia.Controls.Example.slnx       # required only if multiple .slnx exist at the repo root
        secrets:
            checkout_token: ${{ secrets.SUBMODULE_TOKEN }}
            license_key: ${{ secrets.ACCELERATE_LICENSE_KEY }}
            release_manager_api_key: ${{ secrets.RELEASE_MANAGER_API_KEY }}
```

A push to a `release/X.Y.Z` branch packages and uploads the source zip for version
`X.Y.Z`. The `workflow_dispatch` trigger lets you exercise the full pipeline
(stage → scan → zip → verify-build) on demand, and can also repackage historic
releases by setting `ref` to the relevant tag or commit SHA together with the matching
`version`; its `upload` checkbox controls whether that run ships to Release Manager (as
a `generic` artifact) — leave it off for test runs.

The caller repository must:

- Include `build-common` as a submodule at the repository root, pinned to a
  commit that contains `scripts/source-release/stage.sh`.
- Provide an allow-list of customer-facing csproj paths at the configured
  path (default `.github/source-release/projects.txt`).
- Have a `*.slnx` file at the repository root. The staging script reads it
  to know the original solution filename and reuses it for the customer-
  facing slnx so build instructions don't change. If multiple `.slnx` files
  exist at the root, set the `solution_file` input to disambiguate;
  otherwise the first one in filesystem order is picked. `.sln` is not
  currently supported.

The reusable workflow and `stage.sh` rely on Linux tooling — GNU `readlink`,
`jq`, `zip`/`unzip`, `curl` — and only run on `ubuntu-latest`.
