; Bridge Surface add-in -- Inno Setup installer script
; Modelled on the Surface Analysis Extension installer.
;
; Build Release | x64 first. The build output folder is found relative to this
; script (..\bin\Release), so nothing needs editing unless the project moves.

#define MyAppName "Bridge Surface"
#define MyAppVersion "0.0.1"
#define MyAppPublisher "AJ Design Studio LTD"
#define MyAppURL "https://ajdesignstudio.co.nz"
#define MyAppDll "BridgeSurfaceAddin.dll"

#define SourceDir AddBackslash(SourcePath) + "..\bin\Release"

[Setup]
; The INSTALLER's own identity (Add/Remove Programs). Deliberately not any of
; the add-in's COM GUIDs (SwAddin.cs, BridgeFeature.cs).
AppId={{812D4326-1652-42CE-9801-ACB847281A59}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DisableProgramGroupPage=yes
; Program Files, and RegAsm writing HKLM\SOFTWARE\SolidWorks\Addins, both need admin.
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64
OutputDir=Output
OutputBaseFilename=BridgeSurfaceSetup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
InfoBeforeFile=WelcomeInfo.txt
LicenseFile=..\LICENSE
UninstallDisplayIcon={app}\{#MyAppDll}

[Files]
Source: "{#SourceDir}\{#MyAppDll}"; DestDir: "{app}"; Flags: ignoreversion
; optional: helps read a crash report from someone else's machine
Source: "{#SourceDir}\BridgeSurfaceAddin.pdb"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

; The 3 SOLIDWORKS interop DLLs, bundled under SOLIDWORKS's own redistribution terms
; (install_dir\api\redist\redist.txt). Without them RegAsm cannot load the add-in at
; install time. The project copies them into bin\Release (Copy Local = True).
Source: "{#SourceDir}\SolidWorks.Interop.sldworks.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\SolidWorks.Interop.swconst.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\SolidWorks.Interop.swpublished.dll"; DestDir: "{app}"; Flags: ignoreversion

Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

[Run]
Filename: "{win}\Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe"; \
    Parameters: "/codebase ""{app}\{#MyAppDll}"""; \
    StatusMsg: "Registering the add-in with SOLIDWORKS..."; \
    Flags: runhidden

[UninstallRun]
; runs BEFORE the files are removed, so RegAsm can still read the DLL
Filename: "{win}\Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe"; \
    Parameters: "/unregister ""{app}\{#MyAppDll}"""; \
    Flags: runhidden; \
    RunOnceId: "UnregisterBridgeSurface"
