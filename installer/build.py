"""Build a per-machine Windows Installer from a self-contained PC Pulse publish."""

import argparse
import hashlib
from pathlib import Path
import subprocess
import uuid
from xml.etree import ElementTree as ET


WIX = "http://schemas.microsoft.com/wix/2006/wi"
FIREWALL = "http://schemas.microsoft.com/wix/FirewallExtension"
PRODUCT_UPGRADE_CODE = "790329BF-9163-493D-AE73-D54CEF459CA1"
COMPONENT_NAMESPACE = uuid.UUID("ecde2510-82ab-48e0-ada3-97fd3ec00477")
ET.register_namespace("", WIX)
ET.register_namespace("fire", FIREWALL)


def element(parent, name, namespace=WIX, **attributes):
    return ET.SubElement(parent, f"{{{namespace}}}{name}", attributes)


def identifier(prefix, path):
    return prefix + hashlib.sha256(path.casefold().encode()).hexdigest()[:24]


def guid(architecture, key):
    return str(uuid.uuid5(COMPONENT_NAMESPACE, architecture + ":" + key.casefold())).upper()


def firewall(component):
    element(component, "FirewallException", FIREWALL, Id="PrivateNetwork",
            Name="PC Pulse (private LAN)", File="DesktopExe", Port="8787", Protocol="tcp",
            Profile="private", Scope="localSubnet", IgnoreFailure="yes")
    vpn = element(component, "FirewallException", FIREWALL, Id="PrivateVpn",
                  Name="PC Pulse (Tailscale)", File="DesktopExe", Port="8787", Protocol="tcp",
                  Profile="all", IgnoreFailure="yes")
    element(vpn, "RemoteAddress", FIREWALL).text = "100.64.0.0/10"


def generate(payload, architecture, version, file):
    root = ET.Element(f"{{{WIX}}}Wix")
    product = element(root, "Product", Id="*", Name="PC Pulse", Version=version,
                      Language="1033", Manufacturer="PC Pulse", UpgradeCode=PRODUCT_UPGRADE_CODE)
    element(product, "Package", InstallerVersion="500", Compressed="yes", InstallScope="perMachine",
            Platform=architecture, Description="PC Pulse monitoring for Windows")
    element(product, "MajorUpgrade", DowngradeErrorMessage="A newer version of PC Pulse is already installed.")
    element(product, "MediaTemplate", EmbedCab="yes", CompressionLevel="high")
    element(product, "Icon", Id="Pulse.ico", SourceFile=str(Path(__file__).parents[1] / "desktop/Assets/Pulse.ico"))
    element(product, "Property", Id="ARPPRODUCTICON", Value="Pulse.ico")
    element(product, "Property", Id="ARPNOMODIFY", Value="1")
    element(product, "UIRef", Id="WixUI_Common")
    ui = element(product, "UI")
    element(ui, "TextStyle", Id="PulseFont", FaceName="Segoe UI", Size="9")
    element(ui, "TextStyle", Id="WixUI_Font_Bigger", FaceName="Segoe UI", Size="12")
    element(ui, "TextStyle", Id="WixUI_Font_Title", FaceName="Segoe UI", Size="10", Bold="yes")
    element(ui, "Property", Id="DefaultUIFont", Value="PulseFont")
    for dialog in ("ErrorDlg", "FatalError", "FilesInUse", "MsiRMFilesInUse", "PrepareDlg",
                   "ProgressDlg", "ResumeDlg", "UserExit"):
        element(ui, "DialogRef", Id=dialog)
    element(ui, "Publish", Dialog="ExitDialog", Control="Finish", Event="EndDialog",
            Value="Return", Order="999").text = "1"
    welcome = element(ui, "Dialog", Id="InstallPcPulse", Width="370", Height="150", Title="PC Pulse Setup")
    element(welcome, "Control", Id="Heading", Type="Text", X="20", Y="20", Width="330",
            Height="19", Text="Install PC Pulse for Windows")
    element(welcome, "Control", Id="Description", Type="Text", X="20", Y="49", Width="330",
            Height="38", Text="Sets up the tray app, desktop and Start menu shortcuts, and scoped LAN and Tailscale firewall rules.")
    install = element(welcome, "Control", Id="Confirm", Type="PushButton", X="228", Y="115", Width="56",
                      Height="18", Default="yes", Text="Install")
    cancel = element(welcome, "Control", Id="Cancel", Type="PushButton", X="295", Y="115", Width="56",
                     Height="18", Cancel="yes", Text="Cancel")
    element(install, "Publish", Event="EndDialog", Value="Return").text = "1"
    element(cancel, "Publish", Event="EndDialog", Value="Exit").text = "1"
    sequence = element(ui, "InstallUISequence")
    element(sequence, "Show", Dialog="InstallPcPulse", Before="ProgressDlg").text = "NOT Installed AND UILevel >= 5"

    target = element(product, "Directory", Id="TARGETDIR", Name="SourceDir")
    main = element(target, "Directory", Id="ProgramFiles64Folder")
    app = element(main, "Directory", Id="INSTALLDIR", Name="PC Pulse")
    element(target, "Directory", Id="ProgramMenuFolder")
    element(target, "Directory", Id="DesktopFolder")
    feature = element(product, "Feature", Id="PcPulseFeature", Title="PC Pulse", Level="1")
    directories = {".": app}
    for item in sorted(payload.rglob("*")):
        if not item.is_file() or item.suffix.lower() == ".pdb":
            continue
        relative = item.relative_to(payload)
        for parent in reversed(relative.parents):
            key = parent.as_posix()
            if key not in directories:
                directories[key] = element(directories[parent.parent.as_posix()], "Directory",
                                           Id=identifier("D", key), Name=parent.name)
        key = relative.as_posix()
        component_id = identifier("C", key)
        component = element(directories[relative.parent.as_posix()], "Component", Id=component_id,
                            Guid=guid(architecture, key), Win64="yes")
        primary = key == "PcPulse.Desktop.exe"
        installed = element(component, "File", Id="DesktopExe" if primary else identifier("F", key),
                            Name=relative.name, Source=str(item), KeyPath="yes")
        if primary:
            element(installed, "Shortcut", Id="OpenPcPulse", Directory="ProgramMenuFolder",
                    Name="PC Pulse", WorkingDirectory="INSTALLDIR", Advertise="yes", Icon="Pulse.ico")
            element(installed, "Shortcut", Id="OpenPcPulseDesktop", Directory="DesktopFolder",
                    Name="PC Pulse", WorkingDirectory="INSTALLDIR", Advertise="yes", Icon="Pulse.ico")
            firewall(component)
        element(feature, "ComponentRef", Id=component_id)
    ET.indent(root)
    ET.ElementTree(root).write(file, encoding="utf-8", xml_declaration=True)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--wix", required=True, type=Path)
    parser.add_argument("--payload", required=True, type=Path)
    parser.add_argument("--architecture", choices=("x64", "arm64"), default="x64")
    parser.add_argument("--version", default="0.6.0")
    parser.add_argument("--out", required=True, type=Path)
    args = parser.parse_args()
    payload = args.payload.resolve(strict=True)
    if not (payload / "PcPulse.Desktop.exe").is_file():
        raise SystemExit("The publish folder is missing PcPulse.Desktop.exe")
    wix = args.wix.resolve(strict=True)
    output = args.out.resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    build = Path(__file__).parent / "build"
    build.mkdir(exist_ok=True)
    source = build / f"PcPulse-{args.architecture}.wxs"
    compiled = source.with_suffix(".wixobj")
    generate(payload, args.architecture, args.version, source)
    subprocess.run([str(wix / "candle.exe"), "-nologo", "-arch", args.architecture,
                    "-ext", "WixFirewallExtension", "-out", str(compiled), str(source)], check=True)
    subprocess.run([str(wix / "light.exe"), "-nologo", "-ext", "WixUIExtension", "-ext", "WixFirewallExtension",
                    "-out", str(output), str(compiled)],
                   check=True)


if __name__ == "__main__":
    main()
