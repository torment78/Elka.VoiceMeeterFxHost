#define PriorityRegistryRoot "HKCU"
#define TestRoot "SOFTWARE\ElkaSoft\Tests\InstallerPriorityChecks"
#define PriorityIfeoBase TestRoot + "\Images"
#define PriorityBackupBase TestRoot + "\Backups"

[Setup]
AppName=FX Host installer priority checks
AppVersion=1
DefaultDirName={tmp}\ElkaPriorityChecks
CreateAppDir=no
Uninstallable=no
PrivilegesRequired=lowest
OutputBaseFilename=InstallerPriorityChecks

[Code]
var
  InstalledNames: string;
  Checks: Integer;

function VoicemeeterExecutableExists(const ExecutableName: string): Boolean;
begin
  Result := Pos(';' + ExecutableName + ';', InstalledNames) > 0;
end;

#include "..\..\installer\VoicemeeterPriority.iss"

procedure Check(Condition: Boolean; const Message: string);
begin
  Checks := Checks + 1;
  if not Condition then RaiseException(Message);
end;

procedure CheckValue(const Key, Name: string; Expected: LongWord);
var
  Value: LongWord;
begin
  Check(RegQueryDWordValue(PriorityRoot, Key, Name, Value), 'Missing DWORD: ' + Key + '\' + Name);
  Check(Value = Expected, 'Incorrect DWORD: ' + Key + '\' + Name);
end;

procedure RunChecks;
var
  Key, Backup, TextValue: string;
  Index: Integer;
begin
  InstalledNames := '';
  Check(not HasInstalledVoicemeeterForPriority, 'Nothing installed');
  Check(ConfigureVoicemeeterPriority(True), 'No-op with no installed editions');
  Check(not HasManagedVoicemeeterPriority, 'No backups for absent editions');

  InstalledNames := ';voicemeeter8x64.exe;';
  Key := PriorityIfeoKey + '\voicemeeter8x64.exe\PerfOptions';
  Backup := PriorityBackupKey + '\voicemeeter8x64.exe';
  Check(RegWriteDWordValue(PriorityRoot, Key, 'IoPriority', 2), 'Seed unrelated option');
  Check(HasInstalledVoicemeeterForPriority, 'Detect installed edition without a running process');
  Check(ConfigureVoicemeeterPriority(True), 'First opt-in');
  CheckValue(Key, 'CpuPriorityClass', 3);
  CheckValue(Backup, 'OriginalPresent', 0);
  Check(HasManagedVoicemeeterPriority, 'Upgrade keeps checkbox checked');
  Check(ConfigureVoicemeeterPriority(True), 'Repeated upgrade');
  CheckValue(Backup, 'OriginalPresent', 0);
  Check(not RegKeyExists(PriorityRoot, PriorityIfeoKey + '\voicemeeter.exe'), 'Do not configure absent editions');
  InstalledNames := '';
  Check(ConfigureVoicemeeterPriority(False), 'Undo still works after VoiceMeeter removal');
  Check(not RegValueExists(PriorityRoot, Key, 'CpuPriorityClass'), 'Remove only the added value');
  CheckValue(Key, 'IoPriority', 2);
  Check(not HasManagedVoicemeeterPriority, 'Backup removed on undo');

  InstalledNames := ';voicemeeter8x64.exe;';
  Check(RegWriteDWordValue(PriorityRoot, Key, 'CpuPriorityClass', 6), 'Seed previous priority');
  Check(ConfigureVoicemeeterPriority(True), 'Override previous priority');
  Check(ConfigureVoicemeeterPriority(True), 'Keep original across upgrades');
  CheckValue(Backup, 'OriginalValue', 6);
  Check(ConfigureVoicemeeterPriority(False), 'Restore previous priority');
  CheckValue(Key, 'CpuPriorityClass', 6);

  Check(RegWriteDWordValue(PriorityRoot, Key, 'CpuPriorityClass', 3), 'Seed pre-existing High');
  Check(ConfigureVoicemeeterPriority(True), 'Pre-existing High opt-in');
  Check(ConfigureVoicemeeterPriority(False), 'Pre-existing High undo');
  CheckValue(Key, 'CpuPriorityClass', 3);

  Check(ConfigureVoicemeeterPriority(True), 'Opt-in before external change');
  Check(RegWriteDWordValue(PriorityRoot, Key, 'CpuPriorityClass', 2), 'External change');
  Check(ConfigureVoicemeeterPriority(False), 'Leave external change alone');
  CheckValue(Key, 'CpuPriorityClass', 2);

  Check(RegWriteStringValue(PriorityRoot, Key, 'CpuPriorityClass', 'external'), 'Seed unsupported type');
  Check(not ConfigureVoicemeeterPriority(True), 'Reject unsupported type instead of overwriting');
  Check(RegQueryStringValue(PriorityRoot, Key, 'CpuPriorityClass', TextValue) and (TextValue = 'external'), 'Preserve unsupported value');
  Check(not HasManagedVoicemeeterPriority, 'No ownership of unsupported value');
  Check(RegDeleteValue(PriorityRoot, Key, 'CpuPriorityClass'), 'Clear fixture');

  Check(RegWriteDWordValue(PriorityRoot, Backup, 'OriginalPresent', 1), 'Seed incomplete backup');
  Check(not ConfigureVoicemeeterPriority(True), 'Do not overwrite incomplete backup');
  Check(not ConfigureVoicemeeterPriority(False), 'Do not undo without original value');
  Check(not RegValueExists(PriorityRoot, Key, 'CpuPriorityClass'), 'Incomplete backup cannot modify priority');
  Check(RegDeleteValue(PriorityRoot, Backup, 'OriginalPresent'), 'Clear incomplete fixture');

  InstalledNames := ';';
  for Index := 0 to 5 do InstalledNames := InstalledNames + PriorityExecutable(Index) + ';';
  Check(ConfigureVoicemeeterPriority(True), 'All six executable variants');
  for Index := 0 to 5 do CheckValue(PriorityIfeoKey + '\' + PriorityExecutable(Index) + '\PerfOptions', 'CpuPriorityClass', 3);
  Check(ConfigureVoicemeeterPriority(False), 'Undo all six executable variants');
  for Index := 0 to 5 do
    Check(not RegValueExists(PriorityRoot, PriorityIfeoKey + '\' + PriorityExecutable(Index) + '\PerfOptions', 'CpuPriorityClass'), 'Every added launch value removed');
end;

function InitializeSetup: Boolean;
var
  Report: string;
  OwnScratch: Boolean;
begin
  Result := False;
  OwnScratch := False;
  try
    Check(not RegKeyExists(HKCU, '{#TestRoot}'), 'Scratch key already exists; refusing to change it');
    OwnScratch := True;
    RunChecks;
    Report := 'PASS: ' + IntToStr(Checks) + ' installer priority checks; scratch HKCU only, no VoiceMeeter or system priority changes.';
  except
    Report := 'FAIL: ' + GetExceptionMessage;
  end;
  if OwnScratch then
    if not RegDeleteKeyIncludingSubkeys(HKCU, '{#TestRoot}') then
      Report := Report + ' FAIL: could not remove scratch registry keys.';
  SaveStringToFile(ExpandConstant('{param:Report|{tmp}\ElkaPriorityChecks.txt}'), Report, False);
  Log(Report);
  { Abort before installing anything; the report is the test result. }
end;
