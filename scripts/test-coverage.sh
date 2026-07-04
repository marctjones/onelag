#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

coverage_dir="tmp/coverage"
summary_path="$coverage_dir/coverage-summary.txt"
overall_min="${ONELAG_COVERAGE_MIN:-30}"
core_min="${ONELAG_CORE_COVERAGE_MIN:-50}"

rm -rf "$coverage_dir"
mkdir -p "$coverage_dir"

dotnet test OneLag.slnx \
  --configuration Release \
  --collect:"XPlat Code Coverage" \
  --results-directory "$coverage_dir"

coverage_files=()
while IFS= read -r -d '' file; do
  coverage_files+=("$file")
done < <(find "$coverage_dir" -name coverage.cobertura.xml -print0)

if [[ ${#coverage_files[@]} -eq 0 ]]; then
  echo "No coverage.cobertura.xml files were produced." >&2
  exit 1
fi

summary="$(
  awk '
    /<class / {
      class=$0
      sub(/^.*<class name="/, "", class)
      sub(/" filename=.*$/, "", class)
      file=$0
      sub(/^.* filename="/, "", file)
      sub(/" line-rate=.*$/, "", file)
      if (class ~ /^OneLag[.]Core/ && file !~ /^OneLag[.]Core\//) file="OneLag.Core/" file
      if (class ~ /^OneLag[.]Cli/ && file !~ /^OneLag[.]Cli\//) file="OneLag.Cli/" file
      if (class ~ /^OneLag[.]Windows/ && file !~ /^OneLag[.]Windows\//) file="OneLag.Windows/" file
    }
    /<line number=/ {
      number=$0
      sub(/^.* number="/, "", number)
      sub(/" hits=.*$/, "", number)
      hits=$0
      sub(/^.* hits="/, "", hits)
      sub(/" branch=.*$/, "", hits)
      key=file ":" number
      valid[key]=1
      if (file ~ /^OneLag[.]Core\//) core_valid[key]=1
      if ((hits + 0) > 0) {
        covered[key]=1
        if (file ~ /^OneLag[.]Core\//) core_covered[key]=1
      }
    }
    END {
      for (key in valid) total++
      for (key in covered) covered_total++
      for (key in core_valid) core_total++
      for (key in core_covered) core_covered_total++
      overall = total ? covered_total * 100 / total : 0
      core = core_total ? core_covered_total * 100 / core_total : 0
      printf("overall_percent=%.2f\n", overall)
      printf("overall_lines=%d/%d\n", covered_total, total)
      printf("core_percent=%.2f\n", core)
      printf("core_lines=%d/%d\n", core_covered_total, core_total)
    }
  ' "${coverage_files[@]}"
)"

printf "%s\n" "$summary" | tee "$summary_path"

overall_percent="$(printf "%s\n" "$summary" | awk -F= '$1 == "overall_percent" { print $2 }')"
core_percent="$(printf "%s\n" "$summary" | awk -F= '$1 == "core_percent" { print $2 }')"

awk -v actual="$overall_percent" -v minimum="$overall_min" 'BEGIN {
  if ((actual + 0) < (minimum + 0)) {
    printf("Overall coverage %.2f is below minimum %.2f\n", actual, minimum) > "/dev/stderr"
    exit 1
  }
}'

awk -v actual="$core_percent" -v minimum="$core_min" 'BEGIN {
  if ((actual + 0) < (minimum + 0)) {
    printf("Core coverage %.2f is below minimum %.2f\n", actual, minimum) > "/dev/stderr"
    exit 1
  }
}'

echo "Coverage gates passed. Summary: $summary_path"
