#define AppName "DeckWinSave"
#define AppExe "DeckWinSave.exe"

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

#ifndef PublishDir
  #define PublishDir "..\publish-light"
#endif

[Setup]
AppId={{03551B67-6F7E-4286-9CA5-C63D937D985E}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=sol669
DefaultDirName={autopf}\sol669\{#AppName}
DefaultGroupName={#AppName}
DisableDirPage=no
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
OutputDir=..\release
#ifdef Offline
OutputBaseFilename=DeckWinSave-Setup-Offline-v{#AppVersion}
#else
OutputBaseFilename=DeckWinSave-Setup-Light-v{#AppVersion}
#endif
SetupIconFile=..\Assets\DeckWinSave.ico
UninstallDisplayIcon={app}\{#AppExe}
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[CustomMessages]
english.InstallDependency=Installing %1…
english.DependencyFailed=Could not install %1 (exit code %2).
english.DependencyMissing=%1 is still unavailable. Restart Windows and run Setup again.
russian.InstallDependency=Установка %1…
russian.DependencyFailed=Не удалось установить %1 (код %2).
russian.DependencyMissing=%1 пока недоступен. Перезапустите Windows и запустите установщик снова.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; Excludes: "*.pdb,data-location.txt"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
#ifdef Offline
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent runasoriginaluser
#else
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent runasoriginaluser; Check: CanLaunch

[Code]
const
  DotNetName = '.NET 8 Runtime (x64)';
  DotNetFile = 'dotnet-runtime-8.0.30-win-x64.exe';
  DotNetUrl = 'https://builds.dotnet.microsoft.com/dotnet/Runtime/8.0.30/' + DotNetFile;
  DotNetSha256 = 'e40f199c6d5584aff0554c01163c3c8d9ccf6bec3a577e4d967e41070772a1c1';
  AppRuntimeName = 'Windows App Runtime (x64)';
  AppRuntimeFile = 'WindowsAppRuntimeInstall-x64-2.3.1.exe';
  AppRuntimeUrl = 'https://download.microsoft.com/download/cbb0f858-7923-48f0-bc2e-ba8ae187b65c/WindowsAppRuntimeInstall-x64.exe';
  AppRuntimeSha256 = '4011748ddf472b7e856d909fdfb4e9b19c3d23fcd8121039ac91f99d5ffa65db';

var
  DownloadPage: TDownloadWizardPage;
  RestartRequired: Boolean;

function HasDotNet8: Boolean;
var
  InstallDir, FrameworkDir: String;
  Entry: TFindRec;
begin
  Result := False;
  if not RegQueryStringValue(HKLM64, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64',
    'InstallLocation', InstallDir) then
    InstallDir := ExpandConstant('{commonpf64}\dotnet');
  FrameworkDir := AddBackslash(InstallDir) + 'shared\Microsoft.NETCore.App\';
  if FindFirst(FrameworkDir + '8.0.*', Entry) then
  begin
    try
      repeat
        Result := (Pos('-', Entry.Name) = 0) and
          FileExists(FrameworkDir + Entry.Name + '\coreclr.dll');
      until Result or not FindNext(Entry);
    finally
      FindClose(Entry);
    end;
  end;
end;

function HasWindowsAppRuntime: Boolean;
var
  ResultCode: Integer;
  Command: String;
begin
  Command := '-NoProfile -NonInteractive -Command "$packages = Get-AppxPackage -Name Microsoft.WindowsAppRuntime.2 -ErrorAction Stop; ' +
    'foreach ($p in $packages) { if ($p.PublisherId -eq ''8wekyb3d8bbwe'' -and $p.Architecture -eq ''X64'' ' +
    '-and $p.Status -eq ''Ok'' -and $p.Version -ge [version]''2.3.1.0'') { exit 0 } }; exit 1"';
  Result := ExecAsOriginalUser(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    Command, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), SetupMessage(msgPreparingDesc), nil);
end;

function InstallDependency(const Name, FileName, Parameters: String; RunAsUser: Boolean): String;
var
  Executable: String;
  ResultCode: Integer;
  Started: Boolean;
begin
  DownloadPage.SetText(FmtMessage(CustomMessage('InstallDependency'), [Name]), '');
  Executable := ExpandConstant('{tmp}\') + FileName;
  if RunAsUser then
    Started := ExecAsOriginalUser(Executable, Parameters, '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
  else
    Started := Exec(Executable, Parameters, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Log(Name + ' installer result: ' + IntToStr(ResultCode));
  Result := '';
  if not Started or ((ResultCode <> 0) and (ResultCode <> 3010) and (ResultCode <> 1641)) then
    Result := FmtMessage(CustomMessage('DependencyFailed'), [Name, IntToStr(ResultCode)])
  else if ResultCode <> 0 then
    RestartRequired := True;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  NeedDotNet, NeedAppRuntime: Boolean;
begin
  Result := '';
  NeedDotNet := not HasDotNet8;
  NeedAppRuntime := not HasWindowsAppRuntime;
  if not (NeedDotNet or NeedAppRuntime) then
  begin
    Log('Both prerequisites already installed.');
    Exit;
  end;

  DownloadPage.Clear;
  if NeedDotNet then
    DownloadPage.Add(DotNetUrl, DotNetFile, DotNetSha256);
  if NeedAppRuntime then
    DownloadPage.Add(AppRuntimeUrl, AppRuntimeFile, AppRuntimeSha256);
  DownloadPage.Show;
  try
    try
      DownloadPage.Download;
      if NeedDotNet then
      begin
        Result := InstallDependency(DotNetName, DotNetFile, '/install /quiet /norestart', False);
        if Result <> '' then Exit;
        if not HasDotNet8 then
        begin
          NeedsRestart := RestartRequired;
          Result := FmtMessage(CustomMessage('DependencyMissing'), [DotNetName]);
          Exit;
        end;
      end;
      if NeedAppRuntime then
      begin
        Result := InstallDependency(AppRuntimeName, AppRuntimeFile, '--quiet', True);
        if Result <> '' then Exit;
        if not HasWindowsAppRuntime then
        begin
          NeedsRestart := RestartRequired;
          Result := FmtMessage(CustomMessage('DependencyMissing'), [AppRuntimeName]);
        end;
      end;
    except
      Result := GetExceptionMessage;
    end;
  finally
    DownloadPage.Hide;
  end;
end;

function NeedRestart: Boolean;
begin
  Result := RestartRequired;
end;

function CanLaunch: Boolean;
begin
  Result := not RestartRequired;
end;
#endif
