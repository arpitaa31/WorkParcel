#ifndef AppVersion
#define AppVersion "0.2.0"
#endif

#ifndef ReleaseLabel
#define ReleaseLabel "0.2.0-beta.1"
#endif

#ifndef StageDir
#define StageDir "..\artifacts\release\0.2.0-beta.1\staging\installer"
#endif

#ifndef ReleaseDir
#define ReleaseDir "..\artifacts\release\0.2.0-beta.1"
#endif

[Setup]
AppId={{7B06D1B1-0A7E-4A4B-9AA5-1D3B6F5D0A10}
AppName=WorkParcel
AppVersion={#AppVersion}
AppVerName=WorkParcel 0.2.0 Beta
AppPublisher=Arpitaa
AppPublisherURL=https://github.com/arpitaa31/WorkParcel
AppComments=Save a work setup and reopen it later
AppCopyright=Copyright (c) Arpitaa
VersionInfoVersion=0.2.0.0
VersionInfoTextVersion=0.2.0 Beta
VersionInfoCompany=Arpitaa
VersionInfoDescription=Save a work setup and reopen it later
VersionInfoProductName=WorkParcel
VersionInfoProductVersion=0.2.0.0
VersionInfoCopyright=Copyright (c) Arpitaa
DefaultDirName={localappdata}\Programs\WorkParcel
DefaultGroupName=WorkParcel
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#ReleaseDir}
OutputBaseFilename=WorkParcel-Setup-{#ReleaseLabel}
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

