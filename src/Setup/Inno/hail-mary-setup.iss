; Hail Mary Windows installer (Inno Setup 6)
; Build: run build_installer.bat from the repository root.

#ifndef MyAppPublishDir
  #define MyAppPublishDir "..\..\HailMary\bin\Release\net8.0-windows10.0.26100.0\win-x64\publish"
#endif

#ifndef MyAppVersion
  #define MyAppVersion GetVersionNumbersString(AddBackslash(MyAppPublishDir) + "HailMary.exe")
#endif

#define MyAppName "Hail Mary"
#define MyAppExeName "HailMary.exe"
#define MyAppLauncher "Hail Mary.cmd"
#define MyAppPublisher "PepegaSan"
#define MyAppUrl "https://github.com/PepegaSan/OXCO-MEDIA"

; Safety: fail at compile-time if publish output is missing.
#ifnexist AddBackslash(MyAppPublishDir) + MyAppExeName
  #error Publish output not found. Run build_installer.bat first. Path: "{#MyAppPublishDir}\{#MyAppExeName}"
#endif

[Setup]
AppId={{C4E8F2A1-9B3D-4F6E-A812-0D5C9E7B1A42}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppUrl}
AppSupportURL={#MyAppUrl}/issues
AppUpdatesURL={#MyAppUrl}/releases
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
Compression=lzma2
SolidCompression=yes
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
LicenseFile=..\..\..\LICENSE
OutputBaseFilename=HailMary-v{#MyAppVersion}-setup-x64
OutputDir=..\..\..\dist
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
WizardStyle=modern
UninstallDisplayIcon={app}\{#MyAppExeName}
#ifexist AddBackslash(MyAppPublishDir) + "Assets\AppIcon.ico"
SetupIconFile={#MyAppPublishDir}\Assets\AppIcon.ico
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "german"; MessagesFile: "compiler:Languages\German.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "pythonsetup"; Description: "Python virtual environment set up (recommended for background jobs)"; GroupDescription: "Optional:"; Flags: unchecked

[Files]
Source: "{#MyAppPublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "__pycache__\*,*.pyc,*.pyo"
Source: "..\launch-hail-mary.cmd"; DestDir: "{app}"; DestName: "{#MyAppLauncher}"; Flags: ignoreversion
Source: "..\..\..\setup_python.bat"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\..\..\requirements.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\..\..\THIRD_PARTY_NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{group}\{#MyAppName} (with .venv Python)"; Filename: "{app}\{#MyAppLauncher}"; WorkingDir: "{app}"; IconFilename: "{app}\{#MyAppExeName}"
Name: "{group}\Set up Python"; Filename: "{app}\setup_python.bat"; WorkingDir: "{app}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon; WorkingDir: "{app}"

[Run]
Filename: "{app}\setup_python.bat"; Description: "Set up Python now"; Flags: postinstall nowait skipifsilent unchecked; Tasks: pythonsetup

