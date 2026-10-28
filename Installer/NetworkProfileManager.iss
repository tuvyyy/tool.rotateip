#ifndef AppName
  #define AppName "Accre Rotate IP"
#endif
#ifndef AppVersion
  #define AppVersion "1.0.1"
#endif
#ifndef AppId
  #define AppId "{{7ED204ED-59EE-49C7-86F6-1BBA890FE378}"
#endif
#ifndef AppMutexName
  #define AppMutexName "NetworkProfileManager-V1"
#endif
#ifndef PublishRoot
  #define PublishRoot "..\artifacts\publish"
#endif

[Setup]
AppId={#AppId}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Accre
DefaultDirName={localappdata}\Programs\NetworkProfileManager
DefaultGroupName={#AppName}
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
OutputDir=..\artifacts\installer
OutputBaseFilename=NetworkProfileManager_Setup
SetupIconFile=..\src\NetworkProfileManager.App\Assets\app.ico
UninstallDisplayIcon={app}\NetworkProfileManager.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
AppMutex={#AppMutexName}
CloseApplications=no
RestartApplications=no
UninstallDisplayName={#AppName}
VersionInfoVersion={#AppVersion}.0

[Tasks]
Name: desktopicon; Description: "Create a Desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#PublishRoot}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\NetworkProfileManager.exe"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\NetworkProfileManager.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\NetworkProfileManager.exe"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

; User data is outside {app}. Uninstall intentionally preserves profiles, credentials and history.
