; Wisko for Windows: the Inno Setup 6 script for the installer (Wisko-<version>-win-x64-setup.exe).
;
; Built by scripts\build-windows-app.ps1 -Package, which publishes the app (dist\Wisko), makes the example projects
; (dist\Examples) and passes their folders here:
;   ISCC.exe /DAppVersion=0.1.0 /DSourceDir=<dist\Wisko> /DExamplesDir=<dist\Examples> /DOutputDir=<dist>
;            /DIconFile=<branding\AppIcon\Wisko-AppIcon.ico> /DWizardImages=<branding\Installer\Wisko-WizardImage-*.png>
;            /DWizardSmallImages=<branding\Installer\Wisko-WizardSmallImage-*.png> packaging\windows\Wisko.iss
;
; What it does (see packaging\windows\README.md for the reasons and the test procedure):
; - installs for the current user by default (%LOCALAPPDATA%\Programs\Wisko, no administrator rights); "Install for all
;   users" (Program Files) is offered on the first page;
; - Start menu entry; desktop shortcut only when chosen; optionally copies the example projects into the projects
;   folder Wisko uses (%USERPROFILE%\Documents\Wisko\Projects), never replacing a folder that already exists;
; - an upgrade (same AppId) installs over the previous version in the same folder, removing the previous version's
;   program files first; projects, History, preferences and logs are never touched;
; - uninstalling removes the program folder only. Projects (Documents\Wisko), History and %LOCALAPPDATA%\Wisko
;   (preferences, logs) stay, and so do example projects that were copied into the projects folder;
; - while Wisko.exe in the folder runs, setup and the uninstaller refuse before changing anything.
;
; Branding: the setup icon is /DIconFile and the uninstall icon is Wisko.exe's own (Game\Wisko.Editor.csproj
; ApplicationIcon); the wizard images are /DWizardImages (the Finished page) and /DWizardSmallImages (the other pages),
; one file per display scaling, from which Setup picks the exact fit. All follow the files in branding\ (see BRANDING.md).

#ifndef AppVersion
  #error Define AppVersion (build with scripts\build-windows-app.ps1 -Package)
#endif
#ifndef SourceDir
  #error Define SourceDir: the published app folder (dist\Wisko)
#endif
#ifndef ExamplesDir
  #error Define ExamplesDir: the example projects (dist\Examples)
#endif
#ifndef OutputDir
  #define OutputDir "."
#endif
#ifndef IconFile
  #error Define IconFile: the setup icon (branding\AppIcon\Wisko-AppIcon.ico)
#endif
#ifndef WizardImages
  #error Define WizardImages: the large wizard images (branding\Installer\Wisko-WizardImage-*.png)
#endif
#ifndef WizardSmallImages
  #error Define WizardSmallImages: the small wizard images (branding\Installer\Wisko-WizardSmallImage-*.png)
#endif

; /DInstallerTest makes an isolated test build (packaging\windows\README.md, "Testing the installer"): another AppId
; and name, so a test install never meets a real Wisko installation, its shortcuts or its Apps entry; and the example
; projects go to /DTestProjectsFolder instead of the person's projects folder. Release builds never define it.
; /DInstallerTestNoVcRuntime (test builds only) makes setup act as if the Visual C++ runtime were missing.
#ifdef InstallerTestNoVcRuntime
  #ifndef InstallerTest
    #error InstallerTestNoVcRuntime is for installer test builds only
  #endif
#endif
#ifdef InstallerTest
  #ifndef TestProjectsFolder
    #error An installer test build needs TestProjectsFolder
  #endif
  #define AppIdValue "{{6E0B4F51-9C2A-4B7E-8D35-0F6A1C9E2B74}"
  #define AppDisplayName "Wisko Installer Test"
#else
  #define AppIdValue "{{1F1F2E25-3D43-4D51-A531-87DB5CD0F33A}"
  #define AppDisplayName "Wisko"
#endif

[Setup]
; Never change the release AppId: it is how an upgrade finds the installed Wisko.
AppId={#AppIdValue}
AppName={#AppDisplayName}
AppVersion={#AppVersion}
AppVerName={#AppDisplayName} {#AppVersion}
AppPublisher=Wisko Industry Development
AppCopyright=Copyright 2026 Wisko Industry Development
VersionInfoVersion={#AppVersion}.0
VersionInfoProductName=Wisko Engine
VersionInfoProductVersion={#AppVersion}
VersionInfoDescription=Wisko Engine {#AppVersion} setup
VersionInfoCopyright=Copyright 2026 Wisko Industry Development
DefaultDirName={autopf}\{#AppDisplayName}
DefaultGroupName={#AppDisplayName}
DisableProgramGroupPage=yes
UsePreviousAppDir=yes
UsePreviousTasks=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Windows 11 is the supported, tested target of 0.1.0. Windows 10 1809 (within the .NET 10 runtime's range) is accepted
; so it is not blocked, but it is not verified and not advertised as supported (packaging\windows\README.md).
MinVersion=10.0.17763
OutputDir={#OutputDir}
#ifdef InstallerTest
OutputBaseFilename=Wisko-{#AppVersion}-win-x64-setup-INSTALLER-TEST
#else
OutputBaseFilename=Wisko-{#AppVersion}-win-x64-setup
#endif
SetupIconFile={#IconFile}
WizardImageFile={#WizardImages}
WizardSmallImageFile={#WizardSmallImages}
UninstallDisplayIcon={app}\Wisko.exe
UninstallDisplayName={#AppDisplayName} {#AppVersion}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
; A running Wisko is never closed by setup (it could hold unsaved work): setup and the uninstaller refuse while Wisko.exe
; in the chosen folder runs, before changing anything ([Code] below).
CloseApplications=no
RestartApplications=no
SetupLogging=yes
ShowLanguageDialog=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "examples"; Description: "Copy the example projects into Documents\Wisko\Projects (existing projects are never changed)"; GroupDescription: "Example projects:"

[InstallDelete]
; An upgrade first removes the previous version's program files (only file types Wisko itself installs here, so a
; library a newer version no longer uses does not linger). User data never lives in the program folder. Only in a
; folder that already holds Wisko.exe: a folder chosen by hand that holds something else loses nothing.
Type: files; Name: "{app}\*.dll"; Check: WasWiskoFolder
; Wisko's own executables only (never setup's uninstaller, unins000.exe).
Type: files; Name: "{app}\Wisko.exe"; Check: WasWiskoFolder
Type: files; Name: "{app}\createdump.exe"; Check: WasWiskoFolder
Type: files; Name: "{app}\*.json"; Check: WasWiskoFolder
Type: files; Name: "{app}\*.pdb"; Check: WasWiskoFolder
Type: files; Name: "{app}\*.txt"; Check: WasWiskoFolder
Type: files; Name: "{app}\*.md"; Check: WasWiskoFolder
Type: filesandordirs; Name: "{app}\runtimes"; Check: WasWiskoFolder
Type: filesandordirs; Name: "{app}\licenses"; Check: WasWiskoFolder
Type: filesandordirs; Name: "{app}\Examples"; Check: WasWiskoFolder

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#ExamplesDir}\*"; DestDir: "{app}\Examples"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppDisplayName}"; Filename: "{app}\Wisko.exe"; WorkingDir: "{app}"; Comment: "Wisko game engine editor"
Name: "{autodesktop}\{#AppDisplayName}"; Filename: "{app}\Wisko.exe"; WorkingDir: "{app}"; Comment: "Wisko game engine editor"; Tasks: desktopicon

[Run]
Filename: "{app}\Wisko.exe"; Description: "{cm:LaunchProgram,{#AppDisplayName}}"; Flags: nowait postinstall skipifsilent

[Code]
const
  // Microsoft's permanent link to the latest supported Visual C++ v14 Redistributable for x64.
  VisualCppDownload = 'https://aka.ms/vc14/vc_redist.x64.exe';
  // Assimp64.dll and nfd.dll are built with MSVC 14.44; Microsoft requires the runtime to be at least as new.
  VisualCppRequired = 14044;

var
  // Whether the chosen folder held Wisko.exe before this setup changed anything; [InstallDelete] uses it, because its
  // own entries delete Wisko.exe on the way.
  WiskoFolderBeforeInstall: Boolean;
  // What to tell the person about the Microsoft Visual C++ runtime ('' when it is there).
  VisualCppNotice: String;

// The Microsoft Visual C++ runtime in Windows' system folder, as major * 1000 + minor: the lowest version of the DLLs
// Wisko's libraries use, or 0 when one is missing. Wisko makes the same check when it starts (VisualCppRuntime.cs).
function VisualCppRuntimeVersion(): Integer;
var
  Files: array[0..2] of String;
  I, Version: Integer;
  VersionMS, VersionLS: Cardinal;
begin
#ifdef InstallerTestNoVcRuntime
  Result := 0;
  Exit;
#endif
  Files[0] := 'vcruntime140.dll';
  Files[1] := 'vcruntime140_1.dll';
  Files[2] := 'msvcp140.dll';
  Result := 999999;
  for I := 0 to 2 do
  begin
    if not GetVersionNumbers(ExpandConstant('{sys}\') + Files[I], VersionMS, VersionLS) then
    begin
      Result := 0;
      Exit;
    end;
    Version := (VersionMS shr 16) * 1000 + (VersionMS and $FFFF);
    if Version < Result then
      Result := Version;
  end;
end;

// Wisko installs and runs without the runtime; only model import and file dialogs need it. Setup never downloads or
// installs it: it says what is missing and names Microsoft's download.
procedure CheckVisualCppRuntime();
var
  Version: Integer;
begin
  Version := VisualCppRuntimeVersion();
  if Version >= VisualCppRequired then
  begin
    Log(Format('Microsoft Visual C++ runtime %d.%d found', [Version div 1000, Version mod 1000]));
    Exit;
  end;
  if Version = 0 then
    VisualCppNotice := 'Model import and file dialogs in Wisko need the Microsoft Visual C++ Redistributable (x64), which is not installed on this computer.'
  else
    VisualCppNotice := Format('Model import in Wisko needs the Microsoft Visual C++ Redistributable (x64) 14.44 or later; this computer has %d.%d.', [Version div 1000, Version mod 1000]);
  Log(VisualCppNotice + ' Microsoft''s download: ' + VisualCppDownload);
end;

procedure CurPageChanged(CurPageID: Integer);
var
  ErrorCode: Integer;
begin
  if (CurPageID <> wpFinished) or (VisualCppNotice = '') or WizardSilent() then
    Exit;
  if MsgBox(VisualCppNotice + #13#10#13#10 + 'Everything else in Wisko works without it. Microsoft''s official installer for it is:' + #13#10
    + VisualCppDownload + #13#10#13#10 + 'Open this link in your browser now? (Setup does not download or install it.)', mbConfirmation, MB_YESNO) = IDYES then
  begin
    if not ShellExecAsOriginalUser('open', VisualCppDownload, '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode) then
      Log(Format('Could not open %s (error %d)', [VisualCppDownload, ErrorCode]));
  end;
end;

// Whether the chosen folder holds a Wisko installation now.
function IsWiskoFolder(): Boolean;
begin
  Result := FileExists(ExpandConstant('{app}\Wisko.exe'));
end;

// Whether the chosen folder held a Wisko installation when setup started installing (an upgrade): only then are old
// program files removed.
function WasWiskoFolder(): Boolean;
begin
  Result := WiskoFolderBeforeInstall;
end;

// Whether the Wisko.exe in the chosen folder is running: a running program cannot be opened for exclusive writing.
function WiskoIsRunning(): Boolean;
var
  Stream: TFileStream;
begin
  Result := False;
  if not IsWiskoFolder() then
    Exit;
  try
    Stream := TFileStream.Create(ExpandConstant('{app}\Wisko.exe'), fmOpenReadWrite or fmShareExclusive);
    Stream.Free;
  except
    Result := True;
  end;
end;

// Before anything is installed: refuse while Wisko runs from the folder (an upgrade would replace its files).
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  WiskoFolderBeforeInstall := IsWiskoFolder();
  if WiskoIsRunning() then
    Result := 'Wisko is running from ' + ExpandConstant('{app}') + '. Close it (save your work first), then run setup again. Nothing has been changed.';
end;

function InitializeUninstall(): Boolean;
begin
  Result := not WiskoIsRunning();
  if not Result then
    SuppressibleMsgBox('Wisko is running from ' + ExpandConstant('{app}') + '. Close it (save your work first), then uninstall again. Nothing has been changed.', mbError, MB_OK, IDOK);
end;

// The folder Wisko opens by default: %USERPROFILE%\Documents\Wisko\Projects taken literally, exactly as Wisko does
// (not a Documents folder that Windows redirects, for example to OneDrive).
function ProjectsFolder(): String;
begin
#ifdef InstallerTest
  Result := '{#TestProjectsFolder}';
#else
  Result := ExpandConstant('{%USERPROFILE}') + '\Documents\Wisko\Projects';
#endif
end;

function CopyFolder(const Source, Target: String): Boolean;
var
  Find: TFindRec;
begin
  Result := ForceDirectories(Target);
  if not Result then
    Exit;
  if FindFirst(Source + '\*', Find) then
  begin
    try
      repeat
        if (Find.Name <> '.') and (Find.Name <> '..') then
        begin
          if (Find.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
            Result := CopyFolder(Source + '\' + Find.Name, Target + '\' + Find.Name) and Result
          else
            Result := CopyFile(Source + '\' + Find.Name, Target + '\' + Find.Name, True) and Result;
        end;
      until not FindNext(Find);
    finally
      FindClose(Find);
    end;
  end;
end;

// Each example project is copied to a hidden temporary folder and only then renamed into place, so an interrupted copy
// never appears as a project. A folder that already has the project's name (the person's own project, or an earlier
// copy) is never changed.
procedure CopyExampleProjects();
var
  Find: TFindRec;
  Examples, Projects, Name, Staging: String;
begin
  Examples := ExpandConstant('{app}\Examples');
  Projects := ProjectsFolder();
  if not ForceDirectories(Projects) then
  begin
    Log('Could not create the projects folder ' + Projects + '; no example project was copied');
    Exit;
  end;
  if FindFirst(Examples + '\*', Find) then
  begin
    try
      repeat
        Name := Find.Name;
        if (Name <> '.') and (Name <> '..') and ((Find.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0) then
        begin
          if DirExists(Projects + '\' + Name) then
            Log('Example project left as it is (a folder of that name exists): ' + Name)
          else
          begin
            Staging := Projects + '\.wisko-example-copy';
            if DirExists(Staging) then
              DelTree(Staging, True, True, True);
            if CopyFolder(Examples + '\' + Name, Staging) and RenameFile(Staging, Projects + '\' + Name) then
              Log('Example project copied: ' + Name)
            else
            begin
              DelTree(Staging, True, True, True);
              Log('Example project could not be copied: ' + Name);
            end;
          end;
        end;
      until not FindNext(Find);
    finally
      FindClose(Find);
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    if WizardIsTaskSelected('examples') then
      CopyExampleProjects();
    CheckVisualCppRuntime();
  end;
end;
