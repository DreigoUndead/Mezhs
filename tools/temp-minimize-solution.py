from pathlib import Path
import subprocess

raw = subprocess.check_output(["git", "show", "origin/main:Mezhs.sln"])
newline = "\r\n" if b"\r\n" in raw else "\n"
text = raw.decode("utf-8-sig")

agent_web = (
    'Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Mezhs.Agent.Web", "src\\Mezhs.Agent.Web\\Mezhs.Agent.Web.csproj", "{D12F41F7-98C2-4A72-B71C-9271100B1A09}"'
    + newline
    + "EndProject"
    + newline
)
projects = (
    agent_web
    + 'Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Mezhs.Console", "src\\Mezhs.Console\\Mezhs.Console.csproj", "{65113C71-1221-49A5-82CC-B9DD6DA8ADA0}"'
    + newline
    + "EndProject"
    + newline
    + 'Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Mezhs.WhatsApp", "src\\Mezhs.WhatsApp\\Mezhs.WhatsApp.csproj", "{4B959CCB-D991-4B2F-8B87-AE635ED91C68}"'
    + newline
    + "EndProject"
    + newline
)
if text.count(agent_web) != 1:
    raise RuntimeError("Agent Web project marker not found exactly once.")
text = text.replace(agent_web, projects, 1)

marker = "\t\t{D12F41F7-98C2-4A72-B71C-9271100B1A09}.Release|Any CPU.Build.0 = Release|Any CPU"
configs = newline.join([
    marker,
    "\t\t{65113C71-1221-49A5-82CC-B9DD6DA8ADA0}.Debug|Any CPU.ActiveCfg = Debug|Any CPU",
    "\t\t{65113C71-1221-49A5-82CC-B9DD6DA8ADA0}.Debug|Any CPU.Build.0 = Debug|Any CPU",
    "\t\t{65113C71-1221-49A5-82CC-B9DD6DA8ADA0}.Release|Any CPU.ActiveCfg = Release|Any CPU",
    "\t\t{65113C71-1221-49A5-82CC-B9DD6DA8ADA0}.Release|Any CPU.Build.0 = Release|Any CPU",
    "\t\t{4B959CCB-D991-4B2F-8B87-AE635ED91C68}.Debug|Any CPU.ActiveCfg = Debug|Any CPU",
    "\t\t{4B959CCB-D991-4B2F-8B87-AE635ED91C68}.Debug|Any CPU.Build.0 = Debug|Any CPU",
    "\t\t{4B959CCB-D991-4B2F-8B87-AE635ED91C68}.Release|Any CPU.ActiveCfg = Release|Any CPU",
    "\t\t{4B959CCB-D991-4B2F-8B87-AE635ED91C68}.Release|Any CPU.Build.0 = Release|Any CPU",
])
if text.count(marker) != 1:
    raise RuntimeError("Agent Web configuration marker not found exactly once.")
text = text.replace(marker, configs, 1)
Path("Mezhs.sln").write_bytes(text.encode("utf-8"))
