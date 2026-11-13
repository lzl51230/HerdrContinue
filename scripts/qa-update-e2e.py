#!/usr/bin/env python3
"""Build isolated versions, then exercise the actual GUI update button and restart."""
import argparse, hashlib, json, os, pathlib, plistlib, shutil, subprocess, sys, time, zipfile
p=argparse.ArgumentParser()
p.add_argument('--output',required=True)
p.add_argument('--base-app')
p.add_argument('--dotnet',default='dotnet')
p.add_argument('--prepare',action='store_true')
p.add_argument('--case',choices=['success-running','success-stopped','corrupt','startup-failure','cancel-authorization'])
p.add_argument('--target',help='Already prepared installation in a protected directory')
p.add_argument('--timeout',type=int,default=240)
a=p.parse_args(); root=pathlib.Path(a.output).resolve(); root.mkdir(parents=True,exist_ok=True)
mac=sys.platform=='darwin'; rid='osx-arm64' if mac else 'win-x64'
def copy(source,target):
 if mac: subprocess.run(['ditto',str(source),str(target)],check=True)
 else: shutil.copytree(source,target,dirs_exist_ok=True)
if a.prepare:
 if not a.base_app: p.error('--prepare requires --base-app')
 repo=pathlib.Path(__file__).resolve().parents[1]; source=root/'source'
 source.mkdir(exist_ok=True)
 for name in ['src','native','assets','scripts']:
  shutil.copytree(repo/name,source/name,ignore=shutil.ignore_patterns('bin','obj','__pycache__'),dirs_exist_ok=True)
 for name in ['Directory.Build.props','global.json']: shutil.copy2(repo/name,source/name)
 if mac:
  (source/'.tools').mkdir(exist_ok=True)
  cache=source/'.tools/avalonia-native'
  if not cache.exists(): cache.symlink_to(repo/'.tools/avalonia-native',target_is_directory=True)
 prepared={}
 for version in ['1.6.1','1.6.2']:
  bundle=root/version/('Herdr Auto Continue.app' if mac else 'app')
  copy(pathlib.Path(a.base_app).resolve(),bundle)
  output=bundle/'Contents/MacOS' if mac else bundle
  with (root/f'build-{version}.log').open('w') as log:
   subprocess.run([a.dotnet,'publish',str(source/'src/HerdrAutoContinue'),'-c','Debug','-r',rid,'--self-contained','true',f'-p:Version={version}','-p:DebugType=None','-o',str(output),'--tl:off'],cwd=source,stdout=log,stderr=subprocess.STDOUT,check=True)
  if mac:
   plist=bundle/'Contents/Info.plist'; data=plistlib.loads(plist.read_bytes())
   data.update(CFBundleShortVersionString=version,CFBundleVersion=version,CFBundleIdentifier='local.alex.herdr-auto-continue.qa.updates')
   plist.write_bytes(plistlib.dumps(data)); shutil.rmtree(bundle/'Contents/_CodeSignature',ignore_errors=True)
  manifest=bundle/('Contents/Resources/update-manifest.json' if mac else 'update-manifest.json')
  manifest.write_text(json.dumps(dict(Version=version,Runtime=rid,Files=[str(f.relative_to(bundle)).replace('\\','/') for f in bundle.rglob('*') if f.is_file() and f!=manifest])))
  if mac:
   with (root/f'sign-{version}.log').open('w') as log:
    for f in output.rglob('*'):
     if f.is_file() and f.name!='HerdrAutoContinue': subprocess.run(['codesign','--force','--sign','-',str(f)],stdout=log,stderr=subprocess.STDOUT,check=True)
    subprocess.run(['codesign','--force','--sign','-',str(bundle)],stdout=log,stderr=subprocess.STDOUT,check=True)
    subprocess.run(['codesign','--verify','--deep','--strict',str(bundle)],stdout=log,stderr=subprocess.STDOUT,check=True)
  prepared[version]=str(bundle)
 package=root/f'HerdrAutoContinue-{rid}.zip'
 if mac: subprocess.run(['ditto','-c','-k','--sequesterRsrc','--keepParent',prepared['1.6.2'],str(package)],check=True)
 else:
  with zipfile.ZipFile(package,'w',zipfile.ZIP_DEFLATED) as z:
   for f in pathlib.Path(prepared['1.6.2']).rglob('*'):
    if f.is_file(): z.write(f,f.relative_to(prepared['1.6.2']))
 prepared['package']=str(package); (root/'prepared.json').write_text(json.dumps(prepared,indent=2))
 print('Prepared '+str(root/'prepared.json'),flush=True)
if a.case:
 prepared=json.loads((root/'prepared.json').read_text()); case=root/'cases'/a.case
 if (case/'result.json').exists(): raise RuntimeError('Use a fresh output/case directory; refusing stale results')
 case.mkdir(parents=True,exist_ok=True)
 target=pathlib.Path(a.target).resolve() if a.target else case/('Installed.app' if mac else 'installed')
 if not a.target: copy(prepared['1.6.1'],target)
 settings=case/('watchdog-qa-'+a.case); settings.mkdir(exist_ok=True)
 settings_file=settings/'app-settings.json'; settings_file.write_text(json.dumps(dict(version=1,autoStartEnabled=False,silentStartEnabled=True,theme='monokai',cooldownSeconds=13,proxyRecoveryEnabled=False,agentRecoveryEnabled=False)))
 before=settings_file.read_bytes(); running=a.case=='success-running'
 env=os.environ.copy();env.update(WATCHDOG_QA_SESSION='watchdog-qa-'+a.case+'-'+hashlib.sha256(str(root).encode()).hexdigest()[:8],WATCHDOG_QA_SETTINGS_DIR=str(settings),WATCHDOG_QA_SCENARIO='updates-e2e',WATCHDOG_QA_AUTOMATION_DIR=str(case),WATCHDOG_QA_UPDATE_RUNNING=str(running).lower(),WATCHDOG_QA_UPDATE_PACKAGE=prepared['package'],WATCHDOG_QA_UPDATE_BEHAVIOR=a.case if a.case in ['corrupt','startup-failure','cancel-authorization'] else 'success')
 executable=target/('Contents/MacOS/HerdrAutoContinue' if mac else 'HerdrAutoContinue.exe')
 with (case/'application.log').open('w') as log:
  process=subprocess.Popen([str(executable)],env=env,stdout=log,stderr=subprocess.STDOUT,cwd=root)
  deadline=time.monotonic()+a.timeout
  while not (case/'result.json').exists():
   process.poll()  # Reap the exited original app so macOS process-exit observation completes.
   if time.monotonic()>deadline: raise RuntimeError('End-to-end test timed out; inspect '+str(case))
   time.sleep(.2)
  result=json.loads((case/'result.json').read_text()); result['settingsUnchanged']=settings_file.read_bytes()==before
  if not result['settingsUnchanged']: result['success']=False
  if a.case=='cancel-authorization':
   evidence=(case/'application.log').read_text(errors='replace')+'\n'.join(f.read_text(errors='replace') for f in settings.rglob('*.error'))
   result['authorizationCancellationObserved']=any(value in evidence.lower() for value in ['-128','取消','canceled','cancelled'])
   if not result['authorizationCancellationObserved']: result['success']=False
  (case/'result.json').write_text(json.dumps(result,indent=2))
  print(json.dumps(result),flush=True)
  if not result['success']: sys.exit(1)
