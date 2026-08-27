#ifndef AppVersion
#define AppVersion "0.1.0"
#endif

#ifndef StageDir
#define StageDir "..\artifacts\release\0.1.0\staging\installer"
#endif

[Setup]
AppId={{7B06D1B1-0A7E-4A4B-9AA5-1D3B6F5D0A10}
AppName=WorkParcel
AppVersion={#AppVersion}
AppVerName=WorkParcel {#AppVersion}
AppComments=Save a setup. Open it when you return.
DefaultDirName={localappdata}\Programs\WorkParcel
DefaultGroupName=WorkParcel
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\artifacts\release\0.1.0
OutputBaseFilename=WorkParcel-Setup-{#AppVersion}
SetupIconFile={#StageDir}\App\Assets\AppIcon.ico
UninstallDisplayIcon={app}\WorkParcel.exe
UninstallDisplayName=WorkParcel
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
ChangesAssociations=no
PrivilegesRequiredOverridesAllowed=commandline

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#StageDir}\App\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion
Source: "{#StageDir}\BrowserHost\*"; DestDir: "{app}\BrowserHost"; Flags: recursesubdirs ignoreversion
Source: "{#StageDir}\browser-extension\*"; DestDir: "{app}\browser-extension"; Flags: recursesubdirs ignoreversion
Source: "{#StageDir}\Documentation\*"; DestDir: "{app}\Documentation"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{group}\WorkParcel"; Filename: "{app}\WorkParcel.exe"; WorkingDir: "{app}"; IconFilename: "{app}\WorkParcel.exe"; Comment: "Save a setup. Open it when you return."
Name: "{autodesktop}\WorkParcel"; Filename: "{app}\WorkParcel.exe"; WorkingDir: "{app}"; IconFilename: "{app}\WorkParcel.exe"; Comment: "Save a setup. Open it when you return."; Tasks: desktopicon

[Run]
Filename: "{app}\WorkParcel.exe"; Description: "Launch WorkParcel"; Flags: nowait postinstall skipifsilent

