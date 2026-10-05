Unicode true
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"
!define PRODUCT "MusicMachine"
!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\MusicMachine"
Name "${PRODUCT}"
OutFile "${OUTPUT}"
InstallDir "$LOCALAPPDATA\Programs\MusicMachine"
InstallDirRegKey HKCU "${UNINSTALL_KEY}" "InstallLocation"
RequestExecutionLevel user
SetCompressor /SOLID lzma
CRCCheck force
ManifestDPIAware true
VIProductVersion "${VERSION}.0"
VIAddVersionKey "ProductName" "MusicMachine"
VIAddVersionKey "FileDescription" "MusicMachine Setup"
VIAddVersionKey "FileVersion" "${VERSION}"
VIAddVersionKey "LegalCopyright" "MIT License"
!define MUI_ICON "${ICON}"
!define MUI_UNICON "${ICON}"
!define MUI_ABORTWARNING
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_LICENSE "${LICENSE_FILE}"
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_RUN "$INSTDIR\MusicMachine.Desktop.exe"
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"

!macro CheckAppClosed
  ${If} ${FileExists} "$INSTDIR\MusicMachine.Desktop.exe"
    System::Call 'kernel32::CreateFileW(w "$INSTDIR\MusicMachine.Desktop.exe", i 0x40000000, i 0, p 0, i 3, i 0, p 0) p .r0'
    ${If} $0 == -1
      MessageBox MB_OK|MB_ICONSTOP "Close MusicMachine before installing or uninstalling it." /SD IDOK
      SetErrorLevel 2
      Abort
    ${EndIf}
    System::Call 'kernel32::CloseHandle(p r0)'
  ${EndIf}
!macroend
Function .onInit
  SetShellVarContext current
  ${IfNot} ${RunningX64}
    MessageBox MB_OK|MB_ICONSTOP "MusicMachine requires 64-bit Windows." /SD IDOK
    SetErrorLevel 2
    Quit
  ${EndIf}
FunctionEnd
Section "MusicMachine"
  !insertmacro CheckAppClosed
  ClearErrors
  SetOverwrite on
  !include "${INSTALL_FILES}"
  SetOutPath "$INSTDIR"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  ${If} ${Errors}
    MessageBox MB_OK|MB_ICONSTOP "Installation failed. Close MusicMachine and try again." /SD IDOK
    SetErrorLevel 2
    Abort
  ${EndIf}
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayName" "MusicMachine"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "Publisher" "Isaiah Pettingill"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayIcon" "$INSTDIR\musicmachine.ico"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "UninstallString" '$\"$INSTDIR\Uninstall.exe$\"'
  WriteRegStr HKCU "${UNINSTALL_KEY}" "QuietUninstallString" '$\"$INSTDIR\Uninstall.exe$\" /S'
  WriteRegStr HKCU "${UNINSTALL_KEY}" "URLInfoAbout" "https://github.com/isaiahpettingill/MusicMachine"
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "EstimatedSize" ${SIZE_KB}
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoRepair" 1
  CreateShortcut "$SMPROGRAMS\MusicMachine.lnk" "$INSTDIR\MusicMachine.Desktop.exe" "" "$INSTDIR\musicmachine.ico"
  ${If} ${Errors}
    SetErrorLevel 2
    Abort
  ${EndIf}
  SetErrorLevel 0
SectionEnd
Function un.onInit
  SetShellVarContext current
FunctionEnd
Section "Uninstall"
  !insertmacro CheckAppClosed
  ReadRegStr $0 HKCU "${UNINSTALL_KEY}" "InstallLocation"
  ${If} $0 == $INSTDIR
    Delete "$SMPROGRAMS\MusicMachine.lnk"
    DeleteRegKey HKCU "${UNINSTALL_KEY}"
  ${EndIf}
  ; Delete only files owned by this package. User songs are never recursively removed.
  !include "${UNINSTALL_FILES}"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
  SetErrorLevel 0
SectionEnd
