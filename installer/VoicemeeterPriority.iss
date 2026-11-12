#ifndef PriorityRegistryRoot
  #define PriorityRegistryRoot "HKLM64"
#endif
#ifndef PriorityIfeoBase
  #define PriorityIfeoBase "SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options"
#endif
#ifndef PriorityBackupBase
  #define PriorityBackupBase "SOFTWARE\ElkaSoft\VoiceMeeterFxHost\VoicemeeterPriority"
#endif

const
  PriorityRoot = {#PriorityRegistryRoot};
  PriorityIfeoKey = '{#PriorityIfeoBase}';
  PriorityBackupKey = '{#PriorityBackupBase}';
  PriorityHigh = 3;

function PriorityExecutable(Index: Integer): string;
begin
  case Index of
    0: Result := 'voicemeeter.exe';
    1: Result := 'voicemeeter_x64.exe';
    2: Result := 'voicemeeterpro.exe';
    3: Result := 'voicemeeterpro_x64.exe';
    4: Result := 'voicemeeter8.exe';
    5: Result := 'voicemeeter8x64.exe';
  else
    Result := '';
  end;
end;

function HasInstalledVoicemeeterForPriority: Boolean;
var
  Index: Integer;
begin
  Result := False;
  for Index := 0 to 5 do
    if VoicemeeterExecutableExists(PriorityExecutable(Index)) then
      Result := True;
end;

function HasManagedVoicemeeterPriority: Boolean;
var
  Index: Integer;
begin
  Result := False;
  for Index := 0 to 5 do
    if RegValueExists(PriorityRoot, PriorityBackupKey + '\' + PriorityExecutable(Index), 'OriginalPresent') then
      Result := True;
end;

function EnableVoicemeeterPriority(const ExecutableName: string): Boolean;
var
  TargetKey, BackupKey: string;
  OriginalPresent, OriginalValue: LongWord;
begin
  Result := False;
  TargetKey := PriorityIfeoKey + '\' + ExecutableName + '\PerfOptions';
  BackupKey := PriorityBackupKey + '\' + ExecutableName;
  if RegValueExists(PriorityRoot, BackupKey, 'OriginalPresent') then
  begin
    if not RegQueryDWordValue(PriorityRoot, BackupKey, 'OriginalPresent', OriginalPresent) then Exit;
    if OriginalPresent > 1 then Exit;
    if OriginalPresent = 1 then
      if not RegQueryDWordValue(PriorityRoot, BackupKey, 'OriginalValue', OriginalValue) then Exit;
  end
  else
  begin
    OriginalPresent := 0;
    if RegValueExists(PriorityRoot, TargetKey, 'CpuPriorityClass') then
    begin
      { Do not overwrite a value whose type cannot be safely backed up. }
      if not RegQueryDWordValue(PriorityRoot, TargetKey, 'CpuPriorityClass', OriginalValue) then Exit;
      OriginalPresent := 1;
      if not RegWriteDWordValue(PriorityRoot, BackupKey, 'OriginalValue', OriginalValue) then Exit;
    end;
    { Write the backup marker last, before changing Windows' launch setting. }
    if not RegWriteDWordValue(PriorityRoot, BackupKey, 'OriginalPresent', OriginalPresent) then Exit;
  end;
  Result := RegWriteDWordValue(PriorityRoot, TargetKey, 'CpuPriorityClass', PriorityHigh);
  if Result then Log('VoiceMeeter High process priority on next launch: ' + ExecutableName);
end;

function RestoreVoicemeeterPriority(const ExecutableName: string): Boolean;
var
  TargetKey, BackupKey: string;
  OriginalPresent, OriginalValue, CurrentValue: LongWord;
begin
  Result := True;
  TargetKey := PriorityIfeoKey + '\' + ExecutableName + '\PerfOptions';
  BackupKey := PriorityBackupKey + '\' + ExecutableName;
  if not RegValueExists(PriorityRoot, BackupKey, 'OriginalPresent') then Exit;
  Result := False;
  if not RegQueryDWordValue(PriorityRoot, BackupKey, 'OriginalPresent', OriginalPresent) then Exit;
  if OriginalPresent > 1 then Exit;
  if OriginalPresent = 1 then
    if not RegQueryDWordValue(PriorityRoot, BackupKey, 'OriginalValue', OriginalValue) then Exit;

  { Only undo the value we set. Preserve later changes made by other tools. }
  if RegQueryDWordValue(PriorityRoot, TargetKey, 'CpuPriorityClass', CurrentValue) and (CurrentValue = PriorityHigh) then
  begin
    if OriginalPresent = 1 then
      Result := RegWriteDWordValue(PriorityRoot, TargetKey, 'CpuPriorityClass', OriginalValue)
    else
      Result := RegDeleteValue(PriorityRoot, TargetKey, 'CpuPriorityClass');
    if not Result then Exit;
    RegDeleteKeyIfEmpty(PriorityRoot, TargetKey);
    RegDeleteKeyIfEmpty(PriorityRoot, PriorityIfeoKey + '\' + ExecutableName);
  end;
  Result := RegDeleteValue(PriorityRoot, BackupKey, 'OriginalPresent');
  if not Result then Exit;
  if RegValueExists(PriorityRoot, BackupKey, 'OriginalValue') then
    Result := RegDeleteValue(PriorityRoot, BackupKey, 'OriginalValue');
  RegDeleteKeyIfEmpty(PriorityRoot, BackupKey);
end;

function ConfigureVoicemeeterPriority(Enabled: Boolean): Boolean;
var
  Index: Integer;
  ExecutableName: string;
  Succeeded: Boolean;
begin
  Result := True;
  for Index := 0 to 5 do
  begin
    ExecutableName := PriorityExecutable(Index);
    Succeeded := True;
    if Enabled then
    begin
      if VoicemeeterExecutableExists(ExecutableName) then
        Succeeded := EnableVoicemeeterPriority(ExecutableName);
    end
    else
      Succeeded := RestoreVoicemeeterPriority(ExecutableName);
    if not Succeeded then
    begin
      Log('Could not update VoiceMeeter priority setting for ' + ExecutableName);
      Result := False;
    end;
  end;
  RegDeleteKeyIfEmpty(PriorityRoot, PriorityBackupKey);
end;
