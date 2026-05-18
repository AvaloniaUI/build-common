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
repository to wire up source-zip packaging on release publish:

```yaml
name: Source Release

on:
    release:
        types: [published]
    workflow_dispatch:
        inputs:
            version:
                description: 'Version for the test zip (no leading v).'
                required: true
                default: 0.0.0-test1

concurrency:
    group: source-release-${{ github.event.release.tag_name || inputs.version }}
    cancel-in-progress: false

jobs:
    source-release:
        uses: AvaloniaUI/build-common/.github/workflows/source-release.yml@main
        with:
            project_name: Avalonia.Controls.Example
            version: ${{ inputs.version }}                       # empty on release events
            upload_to_s3: ${{ github.event_name == 'release' }}  # only upload real releases
            # allow_list: .github/source-release/projects.txt    # default
        secrets:
            checkout_token: ${{ secrets.SUBMODULE_TOKEN }}
            license_key: ${{ secrets.ACCELERATE_LICENSE_KEY }}
            aws_access_key_id: ${{ secrets.AWS_ACCESS_KEY_ID }}
            aws_secret_access_key: ${{ secrets.AWS_SECRET_ACCESS_KEY }}
            aws_region: ${{ secrets.AWS_REGION }}
            s3_bucket: ${{ secrets.SOURCE_S3_BUCKET }}
```

The `workflow_dispatch` trigger lets you exercise the full pipeline (stage → scan → zip → verify-build) on demand without publishing a release. When dispatched, `version` is used in place of the release tag and `upload_to_s3` is false so the test zip stays in workflow artifacts only.

The caller repository must:

- Include `build-common` as a submodule at the repository root, pinned to a
  commit that contains `scripts/source-release/stage.sh`.
- Provide an allow-list of customer-facing csproj paths at the configured
  path (default `.github/source-release/projects.txt`).
