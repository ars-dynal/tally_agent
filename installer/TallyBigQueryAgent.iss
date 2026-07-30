; ────────────────────────────────────────────────────────────────────
;  Tally BigQuery Agent — Inno Setup 6 installer
;  Output: "Tally BigQuery Agent Setup.exe"
;
;  Build order (see build\build.ps1):
;    1. dotnet publish the three projects (self-contained win-x64)
;    2. ISCC.exe installer\TallyBigQueryAgent.iss
;
;  Behaviour:
;   • fresh install → config wizard pages → connection tests → save
;     config (DPAPI) → install + start Windows Service
;   • upgrade (config.json already present) → skips config pages,
;     stops service, replaces binaries, restarts service, preserves
;     ProgramData (config, queue, checkpoints, logs)
;   • uninstall → stops + deletes service, asks whether to keep data
;   • closing the installer never stops the service (SCM-owned)
; ────────────────────────────────────────────────────────────────────

#define MyAppName "Tally BigQuery Agent"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "Dynalektric"
#define MyServiceName "TallyBigQueryAgent"
#define MyServiceDisplay "Tally BigQuery Data Sync Agent"
#define PublishDir "..\publish"

[Setup]
AppId={{7E2C9A41-3B6D-4F1E-9C58-TALLYBQAGENT}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\Tally BigQuery Agent
DefaultGroupName=Tally BigQuery Agent
DisableProgramGroupPage=yes
OutputBaseFilename=Tally BigQuery Agent Setup
OutputDir=..\dist
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
WizardStyle=modern
SetupIconFile=assets\icon.ico
UninstallDisplayIcon={app}\manager\TallyAgent.Manager.exe
UninstallDisplayName={#MyAppName}
AppMutex=TallyBigQueryAgentSetupMutex
CloseApplications=no
; version info shown in Apps & Features
VersionInfoVersion={#MyAppVersion}
VersionInfoDescription=Tally to BigQuery data sync agent

[Files]
Source: "{#PublishDir}\service\*"; DestDir: "{app}\service"; Flags: ignoreversion recursesubdirs
Source: "{#PublishDir}\cli\*";     DestDir: "{app}\cli";     Flags: ignoreversion recursesubdirs
Source: "{#PublishDir}\manager\*"; DestDir: "{app}\manager"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{group}\Tally BigQuery Agent Manager"; Filename: "{app}\manager\TallyAgent.Manager.exe"
Name: "{group}\Agent Log Folder"; Filename: "{commonappdata}\TallyBigQueryAgent\Logs"

[Registry]
; Windows Event Log source for the service
Root: HKLM; Subkey: "SYSTEM\CurrentControlSet\Services\EventLog\Application\{#MyServiceName}"; \
  ValueType: expandsz; ValueName: "EventMessageFile"; \
  ValueData: "%SystemRoot%\System32\mscoree.dll"; Flags: uninsdeletekey

[Run]
Filename: "{app}\manager\TallyAgent.Manager.exe"; Description: "Launch the management console"; \
  Flags: postinstall nowait skipifsilent

[Code]
var
  TallyPage: TInputQueryWizardPage;
  DatasetsPage: TInputOptionWizardPage;
  CloudPage: TInputQueryWizardPage;
  EnvPage: TInputOptionWizardPage;
  NotifyPage: TInputQueryWizardPage;
  NotifyOptsPage: TInputOptionWizardPage;
  IsUpgrade: Boolean;

function ConfigPath(): String;
begin
  Result := ExpandConstant('{commonappdata}\TallyBigQueryAgent\config.json');
end;

function CliPath(): String;
begin
  Result := ExpandConstant('{app}\cli\TallyAgent.Cli.exe');
end;

// ── wizard pages ────────────────────────────────────────────────

procedure InitializeWizard();
begin
  IsUpgrade := FileExists(ConfigPath());

  TallyPage := CreateInputQueryPage(wpSelectDir,
    'Tally Settings', 'How should the agent connect to TallyPrime?',
    'The agent talks to the Tally XML server over HTTP on the local machine or LAN.');
  TallyPage.Add('Tally server IP or hostname:', False);
  TallyPage.Add('Tally port:', False);
  TallyPage.Add('Tally company name (blank = auto-discover):', False);
  TallyPage.Add('Extraction start date (yyyy-mm-dd):', False);
  TallyPage.Add('Sync frequency in minutes:', False);
  TallyPage.Values[0] := '127.0.0.1';
  TallyPage.Values[1] := '9000';
  TallyPage.Values[2] := '';
  TallyPage.Values[3] := '';
  TallyPage.Values[4] := '15';

  DatasetsPage := CreateInputOptionPage(TallyPage.ID,
    'Data Extraction Options', 'Which Tally datasets should be synced?',
    'All options can be changed later from the management console.', False, False);
  DatasetsPage.Add('Enable automatic company discovery');
  DatasetsPage.Add('Extract masters (ledgers, groups, stock items, ...)');
  DatasetsPage.Add('Extract vouchers (all transaction types)');
  DatasetsPage.Add('Extract inventory (stock groups, godowns, movements)');
  DatasetsPage.Add('Extract GST data (rates, sales/purchase registers)');
  DatasetsPage.Add('Extract cost centres and allocations');
  DatasetsPage.Values[0] := True;
  DatasetsPage.Values[1] := True;
  DatasetsPage.Values[2] := True;
  DatasetsPage.Values[3] := True;
  DatasetsPage.Values[4] := True;
  DatasetsPage.Values[5] := True;

  CloudPage := CreateInputQueryPage(DatasetsPage.ID,
    'Cloud Settings', 'Where should the extracted data be uploaded?',
    'These values are issued by your administrator. The API token is stored encrypted (Windows DPAPI).');
  CloudPage.Add('Cloud ingestion API URL (https://...):', False);
  CloudPage.Add('Agent ID:', False);
  CloudPage.Add('Company ID:', False);
  CloudPage.Add('API authentication token:', True);  { masked }
  CloudPage.Values[0] := '';
  CloudPage.Values[1] := UpperCase(GetComputerNameString());
  CloudPage.Values[2] := '';
  CloudPage.Values[3] := '';

  EnvPage := CreateInputOptionPage(CloudPage.ID,
    'Environment', 'Which environment is this agent part of?',
    'Production agents only accept approved releases and require HTTPS.', True, False);
  EnvPage.Add('Development');
  EnvPage.Add('Testing');
  EnvPage.Add('Production');
  EnvPage.Values[2] := True;

  NotifyPage := CreateInputQueryPage(EnvPage.ID,
    'Notification Settings', 'Where should errors be reported?',
    'Critical errors are sent immediately; repeated errors are grouped into summaries.');
  NotifyPage.Add('Developer/admin email address:', False);
  NotifyPage.Add('Error notification webhook URL (optional):', False);
  NotifyPage.Add('Google Chat webhook URL (optional):', False);
  NotifyPage.Add('Slack webhook URL (optional):', False);

  NotifyOptsPage := CreateInputOptionPage(NotifyPage.ID,
    'Notification Options', 'Email alerts', '', False, False);
  NotifyOptsPage.Add('Enable email error notifications');
  NotifyOptsPage.Values[0] := True;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  { Upgrades keep the existing configuration — skip all config pages. }
  Result := IsUpgrade and
    ((PageID = TallyPage.ID) or (PageID = DatasetsPage.ID) or
     (PageID = CloudPage.ID) or (PageID = EnvPage.ID) or
     (PageID = NotifyPage.ID) or (PageID = NotifyOptsPage.ID));
end;

// ── validation + connection tests ───────────────────────────────

function IsPositiveInt(const S: String): Boolean;
begin
  Result := (S <> '') and (StrToIntDef(S, -1) > 0);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  ResultCode: Integer;
  Args: String;
begin
  Result := True;

  if (TallyPage <> nil) and (CurPageID = TallyPage.ID) then
  begin
    if Trim(TallyPage.Values[0]) = '' then begin
      MsgBox('Please enter the Tally server IP or hostname.', mbError, MB_OK); Result := False; exit;
    end;
    if not IsPositiveInt(TallyPage.Values[1]) then begin
      MsgBox('Tally port must be a positive number (default 9000).', mbError, MB_OK); Result := False; exit;
    end;
    if not IsPositiveInt(TallyPage.Values[4]) then begin
      MsgBox('Sync frequency must be a positive number of minutes (default 15).', mbError, MB_OK); Result := False; exit;
    end;
  end;

  if (CloudPage <> nil) and (CurPageID = CloudPage.ID) then
  begin
    if (Pos('http', LowerCase(Trim(CloudPage.Values[0]))) <> 1) then begin
      MsgBox('Please enter the cloud ingestion API URL (https://...).', mbError, MB_OK); Result := False; exit;
    end;
    if Trim(CloudPage.Values[1]) = '' then begin
      MsgBox('Please enter the Agent ID.', mbError, MB_OK); Result := False; exit;
    end;
    if Trim(CloudPage.Values[2]) = '' then begin
      MsgBox('Please enter the Company ID.', mbError, MB_OK); Result := False; exit;
    end;
    if Trim(CloudPage.Values[3]) = '' then begin
      MsgBox('Please enter the API authentication token.', mbError, MB_OK); Result := False; exit;
    end;
  end;

  { Connection tests run on the final config page, after files are not yet
    copied — so we test with a temporary copy of the CLI extracted by Setup. }
  if (NotifyOptsPage <> nil) and (CurPageID = NotifyOptsPage.ID) and (not IsUpgrade) then
  begin
    WizardForm.NextButton.Enabled := False;
    try
      { Tally test — CLI not yet installed; test after install instead if missing }
      if FileExists(CliPath()) then
      begin
        Args := 'test-tally --host "' + Trim(TallyPage.Values[0]) + '" --port ' +
                Trim(TallyPage.Values[1]);
        if Trim(TallyPage.Values[2]) <> '' then
          Args := Args + ' --company "' + Trim(TallyPage.Values[2]) + '"';
        Exec(CliPath(), Args, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
        if ResultCode <> 0 then
          if MsgBox('The Tally connection test FAILED.' + #13#10 +
                    'Make sure TallyPrime is running with its XML server enabled.' + #13#10#13#10 +
                    'Continue installing anyway? (The service will keep retrying.)',
                    mbConfirmation, MB_YESNO) = IDNO then begin Result := False; exit; end;
      end;
    finally
      WizardForm.NextButton.Enabled := True;
    end;
  end;
end;

// ── helpers to write config + manage the service ────────────────

function JsonEscape(const S: String): String;
var R: String;
begin
  R := S;
  StringChangeEx(R, '\', '\\', True);
  StringChangeEx(R, '"', '\"', True);
  Result := R;
end;

function BoolStr(B: Boolean): String;
begin
  if B then Result := 'true' else Result := 'false';
end;

function EnvName(): String;
begin
  if EnvPage.Values[0] then Result := 'Development'
  else if EnvPage.Values[1] then Result := 'Testing'
  else Result := 'Production';
end;

procedure WriteAndSaveConfig();
var
  Json, TmpFile: String;
  ResultCode: Integer;
begin
  TmpFile := ExpandConstant('{tmp}\agent-config-plain.json');
  Json :=
    '{' + #13#10 +
    '  "schemaVersion": "1.0",' + #13#10 +
    '  "tally": {' + #13#10 +
    '    "host": "' + JsonEscape(Trim(TallyPage.Values[0])) + '",' + #13#10 +
    '    "port": ' + Trim(TallyPage.Values[1]) + ',' + #13#10 +
    '    "company": "' + JsonEscape(Trim(TallyPage.Values[2])) + '",' + #13#10 +
    '    "extractionStartDate": "' + JsonEscape(Trim(TallyPage.Values[3])) + '",' + #13#10 +
    '    "syncFrequencyMinutes": ' + Trim(TallyPage.Values[4]) + ',' + #13#10 +
    '    "autoDiscoverCompanies": ' + BoolStr(DatasetsPage.Values[0]) + ',' + #13#10 +
    '    "enableMasters": ' + BoolStr(DatasetsPage.Values[1]) + ',' + #13#10 +
    '    "enableVouchers": ' + BoolStr(DatasetsPage.Values[2]) + ',' + #13#10 +
    '    "enableInventory": ' + BoolStr(DatasetsPage.Values[3]) + ',' + #13#10 +
    '    "enableGst": ' + BoolStr(DatasetsPage.Values[4]) + ',' + #13#10 +
    '    "enableCostCentres": ' + BoolStr(DatasetsPage.Values[5]) + #13#10 +
    '  },' + #13#10 +
    '  "cloud": {' + #13#10 +
    '    "ingestionApiUrl": "' + JsonEscape(Trim(CloudPage.Values[0])) + '",' + #13#10 +
    '    "agentId": "' + JsonEscape(Trim(CloudPage.Values[1])) + '",' + #13#10 +
    '    "companyId": "' + JsonEscape(Trim(CloudPage.Values[2])) + '",' + #13#10 +
    '    "apiToken": "' + JsonEscape(Trim(CloudPage.Values[3])) + '",' + #13#10 +
    '    "environment": "' + EnvName() + '"' + #13#10 +
    '  },' + #13#10 +
    '  "notifications": {' + #13#10 +
    '    "adminEmail": "' + JsonEscape(Trim(NotifyPage.Values[0])) + '",' + #13#10 +
    '    "enableEmailAlerts": ' + BoolStr(NotifyOptsPage.Values[0]) + ',' + #13#10 +
    '    "errorWebhookUrl": "' + JsonEscape(Trim(NotifyPage.Values[1])) + '",' + #13#10 +
    '    "googleChatWebhookUrl": "' + JsonEscape(Trim(NotifyPage.Values[2])) + '",' + #13#10 +
    '    "slackWebhookUrl": "' + JsonEscape(Trim(NotifyPage.Values[3])) + '"' + #13#10 +
    '  }' + #13#10 +
    '}';

  SaveStringToFile(TmpFile, Json, False);
  { CLI validates, DPAPI-encrypts secrets, writes config.json, deletes temp file }
  Exec(CliPath(), 'save-config --file "' + TmpFile + '"', '', SW_HIDE,
       ewWaitUntilTerminated, ResultCode);
  DeleteFile(TmpFile);
  if ResultCode <> 0 then
    MsgBox('Warning: the configuration could not be saved automatically.' + #13#10 +
           'You can configure the agent later from the management console.',
           mbInformation, MB_OK);
end;

procedure RunCmd(const Cmd, Args: String);
var ResultCode: Integer;
begin
  Exec(Cmd, Args, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure StopAndDeleteService();
var
  ResultCode, I: Integer;
begin
  Exec('sc.exe', 'stop {#MyServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  { wait up to 30 s for stop }
  for I := 1 to 30 do
  begin
    Exec('sc.exe', 'query {#MyServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    if ResultCode <> 0 then break; { service gone }
    Sleep(1000);
    Exec('cmd.exe', '/c sc query {#MyServiceName} | findstr /i STOPPED', '',
         SW_HIDE, ewWaitUntilTerminated, ResultCode);
    if ResultCode = 0 then break;
  end;
  Exec('sc.exe', 'delete {#MyServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(1000);
end;

procedure InstallAndStartService();
var
  BinPath: String;
begin
  BinPath := ExpandConstant('{app}\service\TallyAgent.Service.exe');

  { create service — auto start, LocalService account }
  RunCmd('sc.exe', 'create {#MyServiceName} binPath= "' + BinPath +
         '" start= auto DisplayName= "{#MyServiceDisplay}" obj= "NT AUTHORITY\LocalService"');
  RunCmd('sc.exe', 'description {#MyServiceName} ' +
         '"Extracts TallyPrime data and uploads it securely to the cloud ingestion API for BigQuery."');
  { delayed-auto would also work; plain auto starts sooner after boot }

  { recovery: restart after 1 min / 5 min / 15 min, reset counter daily,
    and also act on non-crash exits with non-zero code }
  RunCmd('sc.exe', 'failure {#MyServiceName} reset= 86400 actions= restart/60000/restart/300000/restart/900000');
  RunCmd('sc.exe', 'failureflag {#MyServiceName} 1');

  { data folder + ACL for LocalService (SID S-1-5-19) }
  ForceDirectories(ExpandConstant('{commonappdata}\TallyBigQueryAgent\Logs'));
  RunCmd('icacls.exe', '"' + ExpandConstant('{commonappdata}\TallyBigQueryAgent') +
         '" /grant *S-1-5-19:(OI)(CI)F /T /Q');

  RunCmd('sc.exe', 'start {#MyServiceName}');
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  Args: String;
begin
  if CurStep = ssInstall then
  begin
    { upgrade/repair: stop + remove old service before file copy }
    StopAndDeleteService();
  end;

  if CurStep = ssPostInstall then
  begin
    if not IsUpgrade then
    begin
      { post-copy connection tests with the real installed CLI }
      Args := 'test-tally --host "' + Trim(TallyPage.Values[0]) + '" --port ' +
              Trim(TallyPage.Values[1]);
      if Trim(TallyPage.Values[2]) <> '' then
        Args := Args + ' --company "' + Trim(TallyPage.Values[2]) + '"';
      Exec(CliPath(), Args, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      if ResultCode <> 0 then
        MsgBox('Note: Tally is not reachable right now. The service will keep retrying ' +
               'automatically once TallyPrime is running with its XML server enabled.',
               mbInformation, MB_OK);

      Exec(CliPath(), 'test-cloud --url "' + Trim(CloudPage.Values[0]) +
           '" --token "' + Trim(CloudPage.Values[3]) +
           '" --agent-id "' + Trim(CloudPage.Values[1]) +
           '" --company-id "' + Trim(CloudPage.Values[2]) +
           '" --environment ' + EnvName(),
           '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      if ResultCode <> 0 then
        MsgBox('Note: the cloud ingestion API test failed. Extracted data will queue ' +
               'locally and upload automatically once connectivity/credentials are fixed.',
               mbInformation, MB_OK);

      WriteAndSaveConfig();
    end;

    InstallAndStartService();
  end;
end;

// ── uninstall ───────────────────────────────────────────────────

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    StopAndDeleteService();
  end;

  if CurUninstallStep = usPostUninstall then
  begin
    if MsgBox('Keep the agent''s configuration, sync queue and logs?' + #13#10#13#10 +
              'Choose Yes to keep them (recommended if you plan to reinstall), ' +
              'or No to delete everything under C:\ProgramData\TallyBigQueryAgent.',
              mbConfirmation, MB_YESNO) = IDNO then
      DelTree(ExpandConstant('{commonappdata}\TallyBigQueryAgent'), True, True, True);
  end;
end;
