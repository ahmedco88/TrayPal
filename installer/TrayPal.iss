; Inno Setup 6 script for TrayPal. Per-user install, no admin prompt.
#define AppName "TrayPal"
#ifndef AppVersion
  #define AppVersion "1.1.0"
#endif

[Setup]
AppId={{2239E0A1-FE9F-4C48-A942-AE0D9116F4AD}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Ahmed (mrobaidi.com)
AppPublisherURL=https://mrobaidi.com
AppSupportURL=https://mrobaidi.com
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename={#AppName}-Setup
SetupIconFile=..\assets\TrayPal.ico
UninstallDisplayIcon={app}\TrayPal.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=force
LicenseFile=..\LICENSE

[Tasks]
Name: "startup"; Description: "Start {#AppName} when I sign in to Windows"
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "..\dist\TrayPal.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\TrayPal.exe"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\TrayPal.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "TrayPal"; ValueData: """{app}\TrayPal.exe"""; Tasks: startup; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "TrayPal"; Flags: dontcreatekey uninsdeletevalue
Root: HKCU; Subkey: "Software\TrayPal"; Flags: dontcreatekey uninsdeletekey

[Run]
Filename: "{app}\TrayPal.exe"; Description: "Launch {#AppName} now"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "taskkill.exe"; Parameters: "/F /IM TrayPal.exe"; Flags: runhidden; RunOnceId: "KillApp"
