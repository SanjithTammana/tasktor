#Requires -Version 7
<#
Bundles CPython's embeddable distribution into a portable folder as <Root>\worker\python (D-C11, M15).

- The zip is verified with its Sigstore bundle against the 3.13 release manager's signing identity before it is used
  (python.org/downloads/metadata/sigstore); a failed check fails the release.
- The worker's runtime wheels (requirements.txt minus test tools) go to Lib\site-packages, installed by the build's own
  Python of the same minor version, binary wheels only, no bytecode.
- python313._pth lists the stdlib zip, the interpreter dir, Lib\site-packages and .. (the worker packages), because
  the agent runs the worker with -I, which ignores PYTHONPATH and the user site.
- A smoke test imports the worker and its wheels with the bundled interpreter in isolated mode.
#>
param(
    [Parameter(Mandatory)] [string]$Root,
    [string]$Version = '3.13.5',
    [string]$Identity = 'thomas@python.org',
    [string]$Issuer = 'https://accounts.google.com'
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$minor = ($Version -split '\.')[0..1] -join ''
$work = Join-Path ([IO.Path]::GetTempPath()) ('tasktor-py-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $work | Out-Null
$zip = Join-Path $work "python-$Version-embed-amd64.zip"
$base = "https://www.python.org/ftp/python/$Version/python-$Version-embed-amd64.zip"
Invoke-WebRequest -Uri $base -OutFile $zip
Invoke-WebRequest -Uri "$base.sigstore" -OutFile "$zip.sigstore"

python -m pip install --quiet "sigstore>=3,<4"
python -m sigstore verify identity --bundle "$zip.sigstore" --cert-identity $Identity --cert-oidc-issuer $Issuer $zip
Write-Host "Verified python-$Version-embed-amd64.zip (Sigstore, $Identity)"

$py = Join-Path $Root 'worker/python'
New-Item -ItemType Directory -Force $py | Out-Null
Expand-Archive -Path $zip -DestinationPath $py -Force

$site = Join-Path $py 'Lib/site-packages'
$runtime = Join-Path $work 'runtime-requirements.txt'
Get-Content worker/requirements.txt | Where-Object { $_ -notmatch '^\s*(pytest|#|$)' } | Set-Content $runtime
python -m pip install --quiet --no-compile --only-binary=:all: --target $site -r $runtime

# Isolated path for -I: stdlib zip, the interpreter dir, the wheels, and the worker packages one level up.
@("python$minor.zip", '.', 'Lib\site-packages', '..') | Set-Content -Encoding ascii (Join-Path $py "python$minor._pth")

$exe = Join-Path $py 'python.exe'
& $exe -I -c "import sys, numpy, sklearn, pandas, jsonschema, tasktor_worker; assert sys.flags.isolated; print('bundled', sys.version.split()[0], 'numpy', numpy.__version__)"

Get-ChildItem $py -Recurse -Directory -Filter '__pycache__' | Remove-Item -Recurse -Force
Remove-Item -Recurse -Force $work
