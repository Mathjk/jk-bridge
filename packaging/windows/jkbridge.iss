; JK Bridge Windows installer
; Build through scripts/build-installer.ps1 so the payload and output paths are
; supplied explicitly. The default paths are useful for local IDE inspection.

#ifndef MyAppVersion
  #define MyAppVersion "2.2.0"
#endif

#ifndef PayloadDir
  #define PayloadDir "payload"
#endif

#ifndef OutputDir
  #define OutputDir "output"
#endif

#define MyAppName "JK Bridge"
#define MyAppPublisher "JK Bridge contributors"
#define MyAppExeName "JKBridge.exe"
#define MyAppUrl "https://github.com/Mathjk/jk-bridge"

[Setup]
AppId={{F9695C58-7AD0-4EDC-85CC-79D5BCA9D352}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppUrl}
AppSupportURL={#MyAppUrl}/issues
AppUpdatesURL={#MyAppUrl}/releases/latest
DefaultDirName={userappdata}\Programs\JKBridge
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
AllowNoIcons=yes
LicenseFile=..\..\LICENSE
OutputDir={#OutputDir}
OutputBaseFilename=JKBridge-Setup-win-x64-{#MyAppVersion}
SetupIconFile=..\..\src\JKBridge.App\Assets\jkbridge.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
ChangesAssociations=no
ChangesEnvironment=no
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} installer
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}
#ifdef SignToolName
SignTool={#SignToolName}
SignedUninstaller=yes
#else
SignedUninstaller=no
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:TaskDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startup"; Description: "{cm:TaskStartup}"; GroupDescription: "{cm:AdditionalOptions}"; Flags: unchecked

[Files]
Source: "Install-OpenSshClient.ps1"; Flags: dontcopy noencryption
#ifdef SignToolName
Source: "{#PayloadDir}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion signonce
#else
Source: "{#PayloadDir}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
#endif

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "JK Bridge"; ValueData: """{app}\{#MyAppExeName}"" --background"; Tasks: startup

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[CustomMessages]
english.TaskDesktopIcon=Create a &desktop shortcut
english.TaskStartup=Start JK Bridge when I sign in to Windows
english.AdditionalOptions=Startup:
english.PrereqTitle=Prerequisite check
english.PrereqDescription=JK Bridge needs Tailscale and the Windows OpenSSH Client.
english.Detected=Detected
english.Missing=Not detected
english.TailscaleStatus=Tailscale application: %1 (sign-in is completed in Tailscale)
english.OpenSshStatus=OpenSSH Client (ssh.exe and ssh-keygen.exe): %1
english.OpenTailscaleDownload=Open the official Tailscale download page after installation
english.InstallOpenSsh=Install the Windows OpenSSH Client after installation (requires an administrator approval)
english.PrereqMissingWarning=One or more prerequisites will still be missing. JK Bridge can be installed now, but it cannot connect until Tailscale is installed, signed in, and OpenSSH Client is available.%n%nContinue installation?
english.OpenSshInstallFailed=Windows OpenSSH Client was not installed. You can install it later from Settings > Optional features. No JK Bridge files need to be reinstalled.
english.OpenSshInstallSucceeded=Windows OpenSSH Client is installed.
english.OpenSshUacPrompt=Windows will ask for administrator approval to install the OpenSSH Client optional feature.
english.TailscaleOpenFailed=The official Tailscale download page could not be opened. Visit https://tailscale.com/download/windows manually.

chinesesimp.TaskDesktopIcon=创建桌面快捷方式(&D)
chinesesimp.TaskStartup=登录 Windows 时启动 JK Bridge
chinesesimp.AdditionalOptions=开机启动：
chinesesimp.PrereqTitle=环境检查
chinesesimp.PrereqDescription=JK Bridge 需要 Tailscale 和 Windows OpenSSH Client。
chinesesimp.Detected=已检测到
chinesesimp.Missing=未检测到
chinesesimp.TailscaleStatus=Tailscale 程序：%1（登录仍在 Tailscale 中完成）
chinesesimp.OpenSshStatus=OpenSSH Client（ssh.exe 和 ssh-keygen.exe）：%1
chinesesimp.OpenTailscaleDownload=安装结束后打开 Tailscale 官方下载页
chinesesimp.InstallOpenSsh=安装结束后安装 Windows OpenSSH Client（需要管理员确认）
chinesesimp.PrereqMissingWarning=安装后仍将缺少一个或多个必要组件。可以继续安装 JK Bridge，但只有安装并登录 Tailscale、且 OpenSSH Client 可用后才能连接。%n%n是否继续安装？
chinesesimp.OpenSshInstallFailed=Windows OpenSSH Client 未能安装。可以稍后在“设置 > 可选功能”中安装，不需要重新安装 JK Bridge。
chinesesimp.OpenSshInstallSucceeded=Windows OpenSSH Client 已安装。
chinesesimp.OpenSshUacPrompt=Windows 将请求管理员确认，以安装 OpenSSH Client 可选功能。
chinesesimp.TailscaleOpenFailed=无法打开 Tailscale 官方下载页。请手动访问 https://tailscale.com/download/windows。

[Code]
var
  PrerequisitesPage: TWizardPage;
  TailscaleStatusLabel: TNewStaticText;
  OpenSshStatusLabel: TNewStaticText;
  TailscaleDownloadCheckBox: TNewCheckBox;
  OpenSshInstallCheckBox: TNewCheckBox;

function PathContainsFile(const FileName: String): Boolean;
begin
  Result := FileSearch(FileName, ExpandConstant('{%PATH}')) <> '';
end;

function IsTailscaleInstalled: Boolean;
var
  ExecutablePath: String;
begin
  Result :=
    FileExists(ExpandConstant('{commonpf64}\Tailscale\tailscale.exe')) or
    FileExists(ExpandConstant('{commonpf32}\Tailscale\tailscale.exe')) or
    FileExists(ExpandConstant('{userappdata}\Tailscale\tailscale.exe')) or
    PathContainsFile('tailscale.exe');

  if (not Result) and RegQueryStringValue(
      HKLM64,
      'SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\tailscale.exe',
      '',
      ExecutablePath) then
    Result := FileExists(ExecutablePath);

  if (not Result) and RegQueryStringValue(
      HKCU64,
      'SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\tailscale.exe',
      '',
      ExecutablePath) then
    Result := FileExists(ExecutablePath);

  if (not Result) and RegQueryStringValue(
      HKLM32,
      'SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\tailscale.exe',
      '',
      ExecutablePath) then
    Result := FileExists(ExecutablePath);

  if (not Result) and RegQueryStringValue(
      HKCU32,
      'SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\tailscale.exe',
      '',
      ExecutablePath) then
    Result := FileExists(ExecutablePath);
end;

function IsOpenSshInstalled: Boolean;
begin
  Result :=
    ((FileExists(ExpandConstant('{sys}\OpenSSH\ssh.exe'))) and
     (FileExists(ExpandConstant('{sys}\OpenSSH\ssh-keygen.exe')))) or
    (PathContainsFile('ssh.exe') and PathContainsFile('ssh-keygen.exe'));
end;

function StatusText(const IsInstalled: Boolean): String;
begin
  if IsInstalled then
    Result := CustomMessage('Detected')
  else
    Result := CustomMessage('Missing');
end;

procedure RefreshPrerequisitePage;
var
  TailscaleInstalled: Boolean;
  OpenSshInstalled: Boolean;
begin
  TailscaleInstalled := IsTailscaleInstalled;
  OpenSshInstalled := IsOpenSshInstalled;

  TailscaleStatusLabel.Caption := FmtMessage(
    CustomMessage('TailscaleStatus'),
    [StatusText(TailscaleInstalled)]);
  OpenSshStatusLabel.Caption := FmtMessage(
    CustomMessage('OpenSshStatus'),
    [StatusText(OpenSshInstalled)]);

  TailscaleDownloadCheckBox.Visible := not TailscaleInstalled;
  if TailscaleInstalled then
    TailscaleDownloadCheckBox.Checked := False;

  OpenSshInstallCheckBox.Visible := not OpenSshInstalled;
  if OpenSshInstalled then
    OpenSshInstallCheckBox.Checked := False;
end;

procedure InitializeWizard;
begin
  PrerequisitesPage := CreateCustomPage(
    wpSelectTasks,
    CustomMessage('PrereqTitle'),
    CustomMessage('PrereqDescription'));

  TailscaleStatusLabel := TNewStaticText.Create(PrerequisitesPage);
  TailscaleStatusLabel.Parent := PrerequisitesPage.Surface;
  TailscaleStatusLabel.Left := 0;
  TailscaleStatusLabel.Top := ScaleY(8);
  TailscaleStatusLabel.Width := PrerequisitesPage.SurfaceWidth;
  TailscaleStatusLabel.Height := ScaleY(18);
  TailscaleStatusLabel.AutoSize := False;

  TailscaleDownloadCheckBox := TNewCheckBox.Create(PrerequisitesPage);
  TailscaleDownloadCheckBox.Parent := PrerequisitesPage.Surface;
  TailscaleDownloadCheckBox.Left := ScaleX(16);
  TailscaleDownloadCheckBox.Top := TailscaleStatusLabel.Top + ScaleY(28);
  TailscaleDownloadCheckBox.Width := PrerequisitesPage.SurfaceWidth - ScaleX(16);
  TailscaleDownloadCheckBox.Height := ScaleY(20);
  TailscaleDownloadCheckBox.Caption := CustomMessage('OpenTailscaleDownload');
  TailscaleDownloadCheckBox.Checked := False;

  OpenSshStatusLabel := TNewStaticText.Create(PrerequisitesPage);
  OpenSshStatusLabel.Parent := PrerequisitesPage.Surface;
  OpenSshStatusLabel.Left := 0;
  OpenSshStatusLabel.Top := TailscaleDownloadCheckBox.Top + ScaleY(40);
  OpenSshStatusLabel.Width := PrerequisitesPage.SurfaceWidth;
  OpenSshStatusLabel.Height := ScaleY(18);
  OpenSshStatusLabel.AutoSize := False;

  OpenSshInstallCheckBox := TNewCheckBox.Create(PrerequisitesPage);
  OpenSshInstallCheckBox.Parent := PrerequisitesPage.Surface;
  OpenSshInstallCheckBox.Left := ScaleX(16);
  OpenSshInstallCheckBox.Top := OpenSshStatusLabel.Top + ScaleY(28);
  OpenSshInstallCheckBox.Width := PrerequisitesPage.SurfaceWidth - ScaleX(16);
  OpenSshInstallCheckBox.Height := ScaleY(20);
  OpenSshInstallCheckBox.Caption := CustomMessage('InstallOpenSsh');
  OpenSshInstallCheckBox.Checked := False;

  RefreshPrerequisitePage;
end;

procedure CurPageChanged(const CurPageID: Integer);
begin
  if CurPageID = PrerequisitesPage.ID then
    RefreshPrerequisitePage;
end;

function NextButtonClick(const CurPageID: Integer): Boolean;
var
  TailscaleWillRemainMissing: Boolean;
  OpenSshWillRemainMissing: Boolean;
begin
  Result := True;

  if CurPageID <> PrerequisitesPage.ID then
    Exit;

  TailscaleWillRemainMissing :=
    (not IsTailscaleInstalled) and (not TailscaleDownloadCheckBox.Checked);
  OpenSshWillRemainMissing :=
    (not IsOpenSshInstalled) and (not OpenSshInstallCheckBox.Checked);

  if TailscaleWillRemainMissing or OpenSshWillRemainMissing then
    Result := MsgBox(
      CustomMessage('PrereqMissingWarning'),
      mbConfirmation,
      MB_YESNO) = IDYES;
end;

procedure InstallOpenSshClient;
var
  ResultCode: Integer;
  PowerShellPath: String;
  ScriptPath: String;
begin
  if IsOpenSshInstalled then
    Exit;

  MsgBox(CustomMessage('OpenSshUacPrompt'), mbInformation, MB_OK);
  ExtractTemporaryFile('Install-OpenSshClient.ps1');
  PowerShellPath := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
  ScriptPath := ExpandConstant('{tmp}\Install-OpenSshClient.ps1');

  if not ShellExec(
    'runas',
    PowerShellPath,
    '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + ScriptPath + '"',
    '',
    SW_SHOWNORMAL,
    ewWaitUntilTerminated,
    ResultCode) then
  begin
    MsgBox(CustomMessage('OpenSshInstallFailed'), mbError, MB_OK);
    Exit;
  end;

  if (ResultCode = 0) and IsOpenSshInstalled then
    MsgBox(CustomMessage('OpenSshInstallSucceeded'), mbInformation, MB_OK)
  else
    MsgBox(CustomMessage('OpenSshInstallFailed'), mbError, MB_OK);
end;

procedure OpenTailscaleDownloadPage;
var
  ResultCode: Integer;
begin
  if IsTailscaleInstalled then
    Exit;

  if not ShellExec(
    'open',
    'https://tailscale.com/download/windows',
    '',
    '',
    SW_SHOWNORMAL,
    ewNoWait,
    ResultCode) then
    MsgBox(CustomMessage('TailscaleOpenFailed'), mbError, MB_OK);
end;

procedure CurStepChanged(const CurStep: TSetupStep);
begin
  if CurStep <> ssPostInstall then
    Exit;

  { Silent installs never install Windows features or open external pages. }
  if WizardSilent then
    Exit;

  if OpenSshInstallCheckBox.Checked then
    InstallOpenSshClient;

  if TailscaleDownloadCheckBox.Checked then
    OpenTailscaleDownloadPage;
end;

procedure CurUninstallStepChanged(const CurUninstallStep: TUninstallStep);
var
  StartupCommand: String;
  ExpectedQuotedCommand: String;
  ExpectedUnquotedCommand: String;
begin
  if CurUninstallStep <> usUninstall then
    Exit;

  ExpectedQuotedCommand := '"' + ExpandConstant('{app}\{#MyAppExeName}') + '" --background';
  ExpectedUnquotedCommand := ExpandConstant('{app}\{#MyAppExeName}') + ' --background';
  if RegQueryStringValue(
      HKCU,
      'Software\Microsoft\Windows\CurrentVersion\Run',
      'JK Bridge',
      StartupCommand) and
     ((LowerCase(StartupCommand) = LowerCase(ExpectedQuotedCommand)) or
      (LowerCase(StartupCommand) = LowerCase(ExpectedUnquotedCommand))) then
    RegDeleteValue(
      HKCU,
      'Software\Microsoft\Windows\CurrentVersion\Run',
      'JK Bridge');
end;
