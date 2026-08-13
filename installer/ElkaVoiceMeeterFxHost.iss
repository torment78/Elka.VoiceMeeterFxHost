#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef RepoRoot
  #define RepoRoot ".."
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\publish\ElkaVoiceMeeterFxHost\win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\release"
#endif
#ifndef OutputBaseFilename
  #define OutputBaseFilename "ElkaVoiceMeeterFxHostSetup"
#endif

[Setup]
AppId={{35D7677D-46D7-46E7-A5B4-C54D3E442F55}
AppName=Elka VoiceMeeter FX Host
AppVersion={#AppVersion}
AppPublisher=ElkaSoft
AppPublisherURL=https://github.com/torment78/Elka.VoiceMeeterFxHost
AppSupportURL=https://github.com/torment78/Elka.VoiceMeeterFxHost/issues
AppUpdatesURL=https://github.com/torment78/Elka.VoiceMeeterFxHost/releases
DefaultDirName={autopf}\ElkaSoft\VoiceMeeter FX Host
DefaultGroupName=ElkaSoft\Elka VoiceMeeter FX Host
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename={#OutputBaseFilename}
SetupIconFile={#RepoRoot}\src\app-wpf\Assets\VoicemeeterDelay.ico
UninstallDisplayIcon={app}\Elka.VoiceMeeterFxHost.App.exe
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern dark
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
CloseApplications=yes
RestartApplications=no
#ifdef SignInstaller
SignTool=ElkaVoiceMeeterFxHost
SignedUninstaller=yes
#ifdef SignedUninstallerOutputDir
SignedUninstallerDir={#SignedUninstallerOutputDir}
#endif
#else
SignedUninstaller=no
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\ElkaSoft\Elka VoiceMeeter FX Host"; Filename: "{app}\Elka.VoiceMeeterFxHost.App.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\Elka VoiceMeeter FX Host"; Filename: "{app}\Elka.VoiceMeeterFxHost.App.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{sys}\powercfg.exe"; Parameters: "/powerthrottling disable /path ""{app}\Elka.VoiceMeeterFxHost.App.exe"""; StatusMsg: "Disabling Windows power throttling for Elka VoiceMeeter FX Host..."; Flags: runhidden waituntilterminated logoutput; Check: ShouldDisableFxHostPowerThrottling
Filename: "{sys}\powercfg.exe"; Parameters: "/powerthrottling disable /path ""{code:GetVoicemeeterExecutablePath|voicemeeter.exe}"""; StatusMsg: "Disabling Windows power throttling for VoiceMeeter Standard (32-bit)..."; Flags: runhidden waituntilterminated logoutput; Check: ShouldDisableVoicemeeterExecutable('voicemeeter.exe')
Filename: "{sys}\powercfg.exe"; Parameters: "/powerthrottling disable /path ""{code:GetVoicemeeterExecutablePath|voicemeeter_x64.exe}"""; StatusMsg: "Disabling Windows power throttling for VoiceMeeter Standard (64-bit)..."; Flags: runhidden waituntilterminated logoutput; Check: ShouldDisableVoicemeeterExecutable('voicemeeter_x64.exe')
Filename: "{sys}\powercfg.exe"; Parameters: "/powerthrottling disable /path ""{code:GetVoicemeeterExecutablePath|voicemeeterpro.exe}"""; StatusMsg: "Disabling Windows power throttling for VoiceMeeter Banana (32-bit)..."; Flags: runhidden waituntilterminated logoutput; Check: ShouldDisableVoicemeeterExecutable('voicemeeterpro.exe')
Filename: "{sys}\powercfg.exe"; Parameters: "/powerthrottling disable /path ""{code:GetVoicemeeterExecutablePath|voicemeeterpro_x64.exe}"""; StatusMsg: "Disabling Windows power throttling for VoiceMeeter Banana (64-bit)..."; Flags: runhidden waituntilterminated logoutput; Check: ShouldDisableVoicemeeterExecutable('voicemeeterpro_x64.exe')
Filename: "{sys}\powercfg.exe"; Parameters: "/powerthrottling disable /path ""{code:GetVoicemeeterExecutablePath|voicemeeter8.exe}"""; StatusMsg: "Disabling Windows power throttling for VoiceMeeter Potato (32-bit)..."; Flags: runhidden waituntilterminated logoutput; Check: ShouldDisableVoicemeeterExecutable('voicemeeter8.exe')
Filename: "{sys}\powercfg.exe"; Parameters: "/powerthrottling disable /path ""{code:GetVoicemeeterExecutablePath|voicemeeter8x64.exe}"""; StatusMsg: "Disabling Windows power throttling for VoiceMeeter Potato (64-bit)..."; Flags: runhidden waituntilterminated logoutput; Check: ShouldDisableVoicemeeterExecutable('voicemeeter8x64.exe')
Filename: "{app}\Elka.VoiceMeeterFxHost.App.exe"; Description: "{cm:LaunchProgram,Elka VoiceMeeter FX Host}"; Flags: nowait postinstall skipifsilent
[Code]
const
  VoicemeeterUninstallKey = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\VB:Voicemeeter {17359A74-1236-5467}';
  VoicemeeterUninstallKeyWow6432 = 'SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\VB:Voicemeeter {17359A74-1236-5467}';

var
  CachedVoicemeeterInstallDir: string;
  CachedVoicemeeterType: Integer;
  VoicemeeterApiQueried: Boolean;
  PerformanceOptionsPage: TInputOptionWizardPage;

function ExecutableDirectoryFromCommand(const CommandLine: string): string;
var
  Value: string;
  ClosingQuote: Integer;
  ExecutableEnd: Integer;
begin
  Value := Trim(CommandLine);
  Result := '';
  if Value = '' then
    Exit;

  if Value[1] = '"' then
  begin
    Delete(Value, 1, 1);
    ClosingQuote := Pos('"', Value);
    if ClosingQuote > 0 then
      Value := Copy(Value, 1, ClosingQuote - 1);
  end
  else
  begin
    ExecutableEnd := Pos('.exe', Lowercase(Value));
    if ExecutableEnd > 0 then
      Value := Copy(Value, 1, ExecutableEnd + 3);
  end;

  Result := ExtractFileDir(Value);
end;

function FindVoicemeeterInstallDir: string;
var
  UninstallCommand: string;
  Candidate: string;
begin
  if CachedVoicemeeterInstallDir <> '' then
  begin
    Result := CachedVoicemeeterInstallDir;
    Exit;
  end;

  UninstallCommand := '';
  if not RegQueryStringValue(HKLM32, VoicemeeterUninstallKey, 'UninstallString', UninstallCommand) then
    if not RegQueryStringValue(HKLM64, VoicemeeterUninstallKey, 'UninstallString', UninstallCommand) then
      RegQueryStringValue(HKLM64, VoicemeeterUninstallKeyWow6432, 'UninstallString', UninstallCommand);

  if UninstallCommand <> '' then
  begin
    Candidate := ExecutableDirectoryFromCommand(UninstallCommand);
    if DirExists(Candidate) then
      CachedVoicemeeterInstallDir := Candidate;
  end;

  if CachedVoicemeeterInstallDir = '' then
  begin
    Candidate := ExpandConstant('{pf32}\VB\Voicemeeter');
    if DirExists(Candidate) then
      CachedVoicemeeterInstallDir := Candidate;
  end;

  if CachedVoicemeeterInstallDir = '' then
  begin
    Candidate := ExpandConstant('{pf64}\VB\Voicemeeter');
    if DirExists(Candidate) then
      CachedVoicemeeterInstallDir := Candidate;
  end;

  Result := CachedVoicemeeterInstallDir;
end;

function GetVoicemeeterRemoteDllPath(const Param: string): string;
begin
  Result := ExpandConstant('{commonpf32}\VB\Voicemeeter\VoicemeeterRemote.dll');
end;

function VBVMR_Login(): LongInt;
  external 'VBVMR_Login@{commonpf32}\VB\Voicemeeter\VoicemeeterRemote.dll stdcall delayload loadwithalteredsearchpath';
function VBVMR_Logout(): LongInt;
  external 'VBVMR_Logout@{commonpf32}\VB\Voicemeeter\VoicemeeterRemote.dll stdcall delayload loadwithalteredsearchpath';
function VBVMR_IsParametersDirty(): LongInt;
  external 'VBVMR_IsParametersDirty@{commonpf32}\VB\Voicemeeter\VoicemeeterRemote.dll stdcall delayload loadwithalteredsearchpath';
function VBVMR_GetVoicemeeterType(var VoicemeeterType: LongInt): LongInt;
  external 'VBVMR_GetVoicemeeterType@{commonpf32}\VB\Voicemeeter\VoicemeeterRemote.dll stdcall delayload loadwithalteredsearchpath';

function GetRunningVoicemeeterType: Integer;
var
  RemoteDllPath: string;
  LoginResult: LongInt;
  DirtyResult: LongInt;
  TypeResult: LongInt;
  ApiType: LongInt;
  ApiLoggedIn: Boolean;
  Attempt: Integer;
begin
  if VoicemeeterApiQueried then
  begin
    Result := CachedVoicemeeterType;
    Exit;
  end;

  VoicemeeterApiQueried := True;
  CachedVoicemeeterType := 0;
  RemoteDllPath := GetVoicemeeterRemoteDllPath('');
  if (RemoteDllPath = '') or not FileExists(RemoteDllPath) then
  begin
    Log('VoiceMeeter Remote API DLL was not found.');
    Result := 0;
    Exit;
  end;

  LoginResult := -1;
  DirtyResult := -1;
  TypeResult := -1;
  ApiLoggedIn := False;
  try
    LoginResult := VBVMR_Login();
    if LoginResult >= 0 then
    begin
      ApiLoggedIn := True;
      for Attempt := 0 to 9 do
      begin
        DirtyResult := VBVMR_IsParametersDirty();
        ApiType := 0;
        TypeResult := VBVMR_GetVoicemeeterType(ApiType);
        if (TypeResult = 0) and (ApiType >= 1) and (ApiType <= 3) then
        begin
          CachedVoicemeeterType := ApiType;
          Break;
        end;
        Sleep(100);
      end;
    end;
  except
    Log('VoiceMeeter in-process Remote API detection failed: ' + GetExceptionMessage);
  end;

  if ApiLoggedIn then
  begin
    try
      VBVMR_Logout();
    except
      Log('VoiceMeeter Remote API logout failed: ' + GetExceptionMessage);
    end;
  end;

  Log('VoiceMeeter in-process API results: login=' + IntToStr(LoginResult) +
    ', dirty=' + IntToStr(DirtyResult) + ', typeResult=' + IntToStr(TypeResult) +
    ', type=' + IntToStr(CachedVoicemeeterType) + '.');
  Result := CachedVoicemeeterType;
end;
function GetVoicemeeterExecutablePath(const ExecutableName: string): string;
var
  InstallDir: string;
begin
  InstallDir := FindVoicemeeterInstallDir;
  if InstallDir = '' then
    Result := ''
  else
    Result := AddBackslash(InstallDir) + ExecutableName;
end;

function VoicemeeterExecutableExists(const ExecutableName: string): Boolean;
begin
  Result := FileExists(GetVoicemeeterExecutablePath(ExecutableName));
end;

function VoicemeeterTypeName(const VoicemeeterType: Integer): string;
begin
  case VoicemeeterType of
    1: Result := 'VoiceMeeter Standard';
    2: Result := 'VoiceMeeter Banana';
    3: Result := 'VoiceMeeter Potato';
  else
    Result := '';
  end;
end;

function ExecutableMatchesVoicemeeterType(const ExecutableName: string; const VoicemeeterType: Integer): Boolean;
begin
  case VoicemeeterType of
    1: Result := (CompareText(ExecutableName, 'voicemeeter.exe') = 0) or
         (CompareText(ExecutableName, 'voicemeeter_x64.exe') = 0);
    2: Result := (CompareText(ExecutableName, 'voicemeeterpro.exe') = 0) or
         (CompareText(ExecutableName, 'voicemeeterpro_x64.exe') = 0);
    3: Result := (CompareText(ExecutableName, 'voicemeeter8.exe') = 0) or
         (CompareText(ExecutableName, 'voicemeeter8x64.exe') = 0);
  else
    Result := False;
  end;
end;

procedure InitializeWizard;
var
  RunningVoicemeeterType: Integer;
  RunningVoicemeeterName: string;
begin
  RunningVoicemeeterType := GetRunningVoicemeeterType;
  RunningVoicemeeterName := VoicemeeterTypeName(RunningVoicemeeterType);

  PerformanceOptionsPage := CreateInputOptionPage(
    wpSelectTasks,
    'Performance Options',
    'Disable Windows power throttling',
    'Setup already has administrator approval. Select the applications that Windows should keep at full performance.',
    False,
    False);
  PerformanceOptionsPage.Add('Elka VoiceMeeter FX Host (recommended)');
  PerformanceOptionsPage.Values[0] := True;

  if RunningVoicemeeterName <> '' then
  begin
    PerformanceOptionsPage.Add(RunningVoicemeeterName + ' (running; detected by Remote API)');
    PerformanceOptionsPage.Values[1] := False;
  end
  else
  begin
    PerformanceOptionsPage.Add('VoiceMeeter (Remote API found, but no running edition was detected)');
    PerformanceOptionsPage.Values[1] := False;
    PerformanceOptionsPage.CheckListBox.ItemEnabled[1] := False;
  end;
end;

function ShouldDisableFxHostPowerThrottling: Boolean;
begin
  Result := PerformanceOptionsPage.Values[0];
end;

function ShouldDisableVoicemeeterPowerThrottling: Boolean;
begin
  Result := PerformanceOptionsPage.Values[1] and (GetRunningVoicemeeterType > 0);
end;

function ShouldDisableVoicemeeterExecutable(const ExecutableName: string): Boolean;
var
  RunningVoicemeeterType: Integer;
begin
  RunningVoicemeeterType := GetRunningVoicemeeterType;
  Result := ShouldDisableVoicemeeterPowerThrottling and
    ExecutableMatchesVoicemeeterType(ExecutableName, RunningVoicemeeterType) and
    VoicemeeterExecutableExists(ExecutableName);
end;
