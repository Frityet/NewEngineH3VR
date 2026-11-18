#!/usr/bin/env python3
"""Build private macOS native platform inputs from explicitly supplied SDKs."""
import argparse, hashlib, json, os, pathlib, shutil, subprocess

p = argparse.ArgumentParser()
p.add_argument('--csteamworks', required=True, type=pathlib.Path)
p.add_argument('--steam-api', required=True, type=pathlib.Path)
p.add_argument('--fmod-sdk', required=True, type=pathlib.Path, help='FMOD 2.03.15 SDK directory containing api/')
p.add_argument('--output', required=True, type=pathlib.Path, help='new private directory')
a = p.parse_args()
root = pathlib.Path(__file__).resolve().parents[2]
out = a.output.resolve()
if out.exists(): raise SystemExit('Output already exists; choose a new directory')
wrapper = a.csteamworks.resolve(); sdk = a.fmod_sdk.resolve() / 'api'
for protected in (root, wrapper, sdk.parent, a.steam_api.resolve().parent):
    if out.is_relative_to(protected) or protected.is_relative_to(out):
        raise SystemExit('Output must be separate from the checkout and every supplied SDK or Steam input')
if subprocess.check_output(['git','-C',str(wrapper),'rev-parse','HEAD'], text=True).strip() != '407dcba0151cfa52d1814a752e3038f16dbedc4d':
    raise SystemExit('CSteamworks revision differs from the verified SDK 1.41 wrapper')
for args in (['diff','--quiet'], ['diff','--cached','--quiet']):
    if subprocess.run(['git','-C',str(wrapper),*args]).returncode != 0:
        raise SystemExit('CSteamworks tracked sources have local changes')
if '0x00020315' not in (sdk/'core/inc/fmod_common.h').read_text():
    raise SystemExit('FMOD SDK headers must be version 2.03.15')
inputs = {'libsteam_api.dylib': a.steam_api.resolve(), 'libfmod.dylib': sdk/'core/lib/libfmod.dylib', 'libfmodstudio.dylib': sdk/'studio/lib/libfmodstudio.dylib'}
for path in inputs.values(): subprocess.run(['lipo','-verify_arch','arm64',str(path)], check=True)
out.mkdir(parents=True)
for name, src in inputs.items():
    dest = out/name
    shutil.copyfile(src, dest)
    subprocess.run(['install_name_tool','-id','@rpath/'+name,str(dest)], check=True)

def run(args): subprocess.run([str(x) for x in args], check=True)
common = ['clang++','-arch','arm64','-O2','-dynamiclib','-mmacosx-version-min=12.0']
run(common+['-std=c++11','-DPOSIX','-DOSX','-DCSTEAMWORKS_DLLEXPORT','-DUSE_BREAKPAD_HANDLER'] + [f'-I{wrapper/x}' for x in ('steam','src','wrapper')] + [wrapper/'src/steam_api.cpp', root/'runtime/Platform/Steam/legacy_api.cpp',f'-L{out}','-lsteam_api','-Wl,-rpath,@loader_path','-Wl,-install_name,@rpath/libCSteamworks.dylib','-o',out/'libCSteamworks.dylib'])
run(common+['-std=c++17',f'-I{sdk}/core/inc',f'-I{sdk}/studio/inc',root/'runtime/Platform/Fmod/bridge.cpp',root/'runtime/Platform/Fmod/fmod-output.cpp',f'-L{out}','-lfmod','-lfmodstudio',f'-Wl,-reexport_library,{out}/libfmod.dylib',f'-Wl,-reexport_library,{out}/libfmodstudio.dylib','-Wl,-rpath,@loader_path','-Wl,-install_name,@rpath/libNativeH3VRFMOD.dylib','-o',out/'libNativeH3VRFMOD.dylib'])
entries=[]
for name in [*inputs, 'libCSteamworks.dylib', 'libNativeH3VRFMOD.dylib']:
    path=out/name
    run(['lipo','-verify_arch','arm64',path]); run(['codesign','--force','--sign','-',path]); run(['codesign','--verify','--strict',path])
    guid=hashlib.sha256(('NativeH3VR.Platform:'+name).encode()).hexdigest()[:32]
    meta=path.with_name(name+'.meta')
    meta.write_text(f'''fileFormatVersion: 2\nguid: {guid}\nPluginImporter:\n  externalObjects: {{}}\n  serializedVersion: 2\n  isPreloaded: 0\n  isOverridable: 0\n  isExplicitlyReferenced: 0\n  validateReferences: 1\n  platformData:\n  - first:\n      Any:\n    second:\n      enabled: 0\n      settings: {{}}\n  - first:\n      Editor: Editor\n    second:\n      enabled: 0\n      settings:\n        DefaultValueInitialized: true\n  - first:\n      Standalone: OSXUniversal\n    second:\n      enabled: 1\n      settings:\n        CPU: ARM64\n        OS: OSX\n  userData:\n  assetBundleName:\n  assetBundleVariant:\n''')
    for item in (path,meta): entries.append({'Source':item.name,'Destination':'Assets/Plugins/Native/macOS/'+item.name,'Sha256':hashlib.sha256(item.read_bytes()).hexdigest()})
(out/'platform.json').write_text(json.dumps({'Files':entries},indent=2)+'\n')
print('Private platform fragment:',out/'platform.json')
