# CI/CD Quick Start

## Local build

```powershell
cd C:\path\to\TallyBigQueryAgent
choco install innosetup -y
.\build\build.ps1 -Version "1.0.1"
```

Output:

```text
dist\Tally BigQuery Agent Setup.exe
dist\Tally BigQuery Agent Setup.exe.sha256
```

## Upload to GitHub

```powershell
git init
git add .
git commit -m "Initial Tally Agent CI/CD setup"
git branch -M main
git remote add origin https://github.com/<ORG>/<REPOSITORY>.git
git push -u origin main
```

## Create first release

```powershell
git tag v1.0.1
git push origin v1.0.1
```
