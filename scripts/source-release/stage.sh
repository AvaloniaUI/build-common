#!/usr/bin/env bash
#
# Stages a customer-facing copy of the repository for shipping as a source release.
#
# Driven by an allow-list of customer-facing csproj paths. Uses MSBuild
# (`dotnet msbuild -getItem -getProperty -pp`) to enumerate the exact files
# those projects need: @(Compile)/@(None)/@(Content)/@(EmbeddedResource)/
# @(AvaloniaResource)/@(AvaloniaXaml)/@(AdditionalFiles) items, the csproj
# files themselves, the strong-name key (AssemblyOriginatorKeyFile), and all
# .props/.targets imported transitively (discovered via `-pp`, which inlines
# every conventional and explicit import).
#
# Files outside the repo root (the .NET SDK's own targets, etc.) are filtered
# out. A customer-facing slnx that references only the allow-listed projects
# is generated at the staging root, reusing the original slnx's filename so
# customer build instructions don't change.
#
# Requires: bash, dotnet (9.0+ for -getItem/-getProperty), jq (and python3 on
# platforms without GNU `readlink -m`, e.g. macOS).

set -euo pipefail

# Portable replacement for GNU `readlink -m`/`-f` (canonicalize a path, allowing
# non-existent components). BSD/macOS `readlink` has no `-m`, so fall back to
# python3 (present on every GitHub runner) when GNU readlink isn't available —
# letting this script also run on a macOS agent (e.g. to stage mobile TFMs).
canonicalize() {
    if readlink -m / >/dev/null 2>&1; then
        readlink -m "$1"
    else
        python3 -c 'import os,sys; print(os.path.realpath(sys.argv[1]))' "$1"
    fi
}

if [[ $# -lt 3 ]]; then
    cat >&2 <<'EOF'
usage: stage.sh <repo-root> <allow-list> <staging-dir> [solution-file]

  <repo-root>     Path to the repository root.
  <allow-list>    Text file listing one customer-facing csproj per line
                  (paths relative to <repo-root>). Blank lines and lines
                  starting with `#` are ignored.
  <staging-dir>   Output directory. Will be created if missing.
  [solution-file] Optional .slnx filename at <repo-root> to mirror as the
                  customer-facing solution. When omitted, the first .slnx
                  at <repo-root> (filesystem order) is used.
EOF
    exit 64
fi

repo_root=$(cd "$1" && pwd)

# Resolve <allow-list> relative to <repo-root> when not absolute, so the path
# semantics match what's documented above. Check existence explicitly first so a
# missing allow-list gives a clear message rather than a confusing canonicalize error.
case "$2" in
    /*) allow_list="$2" ;;
    *)  allow_list="$repo_root/$2" ;;
esac
if [[ ! -f "$allow_list" ]]; then
    echo "::error::Allow-list file not found at '$2' (resolved to '$allow_list')." >&2
    exit 1
fi
allow_list=$(canonicalize "$allow_list")

staging_dir=$(canonicalize "$3")
solution_file="${4:-}"

mkdir -p "$staging_dir"

echo "Repo root:    $repo_root"
echo "Allow list:   $allow_list"
echo "Staging dir:  $staging_dir"
echo

declare -a projects=()
while IFS= read -r line || [[ -n "$line" ]]; do
    line="${line%%#*}"
    line="${line#"${line%%[![:space:]]*}"}"
    line="${line%"${line##*[![:space:]]}"}"
    [[ -z "$line" ]] && continue
    if [[ ! -f "$repo_root/$line" ]]; then
        echo "::error file=$allow_list::Project does not exist: $line" >&2
        exit 1
    fi
    projects+=("$line")
done < "$allow_list"

if [[ ${#projects[@]} -eq 0 ]]; then
    echo "::error file=$allow_list::Allow-list is empty." >&2
    exit 1
fi

echo "Customer-facing projects (${#projects[@]}):"
printf '  %s\n' "${projects[@]}"
echo

files_raw=$(mktemp)
files_relative=$(mktemp)
tmp_files=("$files_raw" "$files_relative")
trap 'rm -f "${tmp_files[@]}"' EXIT

# Enumerate one project, target framework pair. Source items come from
# `dotnet msbuild -getItem -getProperty` (JSON). Imported props/targets come
# from `-pp` (preprocess), which inlines every import — including conventional
# Directory.Build.props/.targets and explicit <Import>s — and records each
# import's absolute path on its own line as part of the leading comment block.
# This is more reliable than $(MSBuildAllProjects), which modern MSBuild
# populates only partially.
enumerate() {
    local csproj=$1
    local tfm=$2
    echo "  - $csproj  (TargetFramework=$tfm)"

    (cd "$repo_root" && dotnet msbuild "$csproj" \
        -getItem:Compile,None,Content,EmbeddedResource,AvaloniaResource,AvaloniaXaml,AdditionalFiles \
        -getProperty:MSBuildProjectFullPath,AssemblyOriginatorKeyFile \
        -p:TargetFramework="$tfm" \
        -nologo) | jq -r '
            ([.Items // {} | to_entries[].value[] | (.FullPath // .Identity)])
            + ([.Properties.MSBuildProjectFullPath // empty])
            + ([.Properties.AssemblyOriginatorKeyFile // "" | select(. != "")])
            | .[]' >> "$files_raw"

    local pp_file
    pp_file=$(mktemp)
    # Register with the script-level EXIT trap so the temp file is removed
    # even if the dotnet call below fails and triggers `set -e`.
    tmp_files+=("$pp_file")
    (cd "$repo_root" && dotnet msbuild "$csproj" \
        -pp:"$pp_file" \
        -p:TargetFramework="$tfm" \
        -nologo) > /dev/null
    # `dotnet msbuild -pp` writes CRLF line endings; strip CR before matching.
    tr -d '\r' < "$pp_file" | grep -E '^/.*\.(props|targets)$' >> "$files_raw" || true
}

for csproj in "${projects[@]}"; do
    echo "Enumerating $csproj..."
    tfm_json=$(cd "$repo_root" && dotnet msbuild "$csproj" \
        -getProperty:TargetFrameworks,TargetFramework -nologo)
    tfm_list=$(echo "$tfm_json" | jq -r '
        (.Properties.TargetFrameworks // "") as $multi
        | (.Properties.TargetFramework // "") as $single
        | if $multi != "" then $multi else $single end')
    tfm_list="${tfm_list//[[:space:]]/}"
    if [[ -z "$tfm_list" ]]; then
        echo "::error::Project $csproj defines neither TargetFramework nor TargetFrameworks." >&2
        exit 1
    fi
    IFS=';' read -ra tfm_array <<< "$tfm_list"
    for tfm in "${tfm_array[@]}"; do
        [[ -z "$tfm" ]] && continue
        enumerate "$csproj" "$tfm"
    done
done
echo

# Filter to repo-relative paths and dedupe. MSBuild reports .FullPath as
# absolute, but the .Identity fallback can be relative; resolve from $repo_root
# so a non-absolute path resolves the same way the project sees it, regardless
# of this script's CWD.
while IFS= read -r path; do
    [[ -z "$path" ]] && continue
    abs=$(cd "$repo_root" && canonicalize "$path")
    if [[ "$abs" == "$repo_root"/* ]]; then
        echo "${abs#"$repo_root"/}"
    fi
done < "$files_raw" | sort -u > "$files_relative"

count=$(wc -l < "$files_relative")
echo "Collected $count repo-relative source files."
echo

echo "Staging files into $staging_dir..."
while IFS= read -r rel; do
    src="$repo_root/$rel"
    dst="$staging_dir/$rel"
    mkdir -p "$(dirname "$dst")"
    cp -a "$src" "$dst"
done < "$files_relative"

# Generate a customer-facing slnx referencing only the allow-listed projects.
# Reuse the original slnx's filename so customer commands are unchanged. When
# the caller specifies a solution file explicitly, honor it; otherwise pick the
# first .slnx at the repo root (which is non-deterministic if there are several,
# hence the explicit input).
if [[ -n "$solution_file" ]]; then
    orig_slnx="$repo_root/$solution_file"
    if [[ ! -f "$orig_slnx" ]]; then
        echo "::error::Specified solution file not found at $orig_slnx." >&2
        exit 1
    fi
else
    orig_slnx=$(find "$repo_root" -maxdepth 1 -name '*.slnx' -print -quit)
    if [[ -z "$orig_slnx" ]]; then
        echo "::error::No .slnx found at $repo_root." >&2
        exit 1
    fi
fi
slnx_name=$(basename "$orig_slnx")
customer_slnx="$staging_dir/$slnx_name"

{
    echo '<Solution>'
    for csproj in "${projects[@]}"; do
        echo "  <Project Path=\"${csproj//\\//}\" />"
    done
    echo '</Solution>'
} > "$customer_slnx"

echo
echo "Wrote $customer_slnx:"
sed 's/^/  /' "$customer_slnx"
