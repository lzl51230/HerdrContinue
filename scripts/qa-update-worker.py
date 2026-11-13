#!/usr/bin/env python3
"""Exercise actual updater replacement and rollback in disposable signed app copies."""
import argparse, json, pathlib, shutil, subprocess, tempfile, time
p = argparse.ArgumentParser()
p.add_argument('--app', required=True)
p.add_argument('--output', required=True)
a = p.parse_args()
app = pathlib.Path(a.app).resolve()
out = pathlib.Path(a.output).resolve(); out.mkdir(parents=True, exist_ok=True)
mac = app.suffix == '.app'
helper = app / ('Contents/MacOS/Watchdog.Updater' if mac else 'Watchdog.Updater.exe')
manifest = json.loads((app / ('Contents/Resources/update-manifest.json' if mac else 'update-manifest.json')).read_text(encoding='utf-8-sig'))
results = {}
for decision in ['success', 'rollback', 'recovery']:
 with tempfile.TemporaryDirectory(prefix='watchdog-qa-update-') as directory:
  root = pathlib.Path(directory); target = root / ('Installed.app' if mac else 'installed'); stage = root / ('Stage.app' if mac else 'stage')
  if mac:
   subprocess.run(['ditto', str(app), str(target)], check=True); subprocess.run(['ditto', str(app), str(stage)], check=True)
  else:
   shutil.copytree(app, target); shutil.copytree(app, stage)
  if decision != 'recovery': (target / 'user-extra.txt').write_text('preserved')
  tx = root / 'transaction.json'
  tx.write_text(json.dumps(dict(Target=str(target), Stage=str(stage), Backup=str(target)+'.update-backup', Parent=2147483647, Running=False, Version=manifest['Version'])))
  process = subprocess.Popen([str(helper), 'worker', str(tx)], stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
  def wait(suffix):
   until = time.monotonic() + 60
   while not pathlib.Path(str(tx)+suffix).exists():
    if process.poll() is not None or time.monotonic() > until:
     process.kill(); raise RuntimeError(process.communicate()[0].decode(errors='replace') + (pathlib.Path(str(tx)+'.error').read_text() if pathlib.Path(str(tx)+'.error').exists() else 'timeout'))
    time.sleep(.1)
  wait('.ready'); pathlib.Path(str(tx)+'.commit').touch(); wait('.installed')
  if decision == 'recovery':
   process.kill(); process.communicate()
   for suffix in ['.ready', '.commit', '.installed']: pathlib.Path(str(tx)+suffix).unlink()
   pathlib.Path(str(tx)+'.recover').touch()
   process = subprocess.Popen([str(helper), 'worker', str(tx)], stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
   wait('.ready'); pathlib.Path(str(tx)+'.commit').touch(); wait('.installed')
  else: pathlib.Path(str(tx)+'.decision').write_text(decision)
  stdout = process.communicate(timeout=60)[0].decode(errors='replace')
  if process.returncode: raise RuntimeError(stdout)
  phase = pathlib.Path(str(tx)+'.phase').read_text()
  assert phase == ('complete' if decision == 'success' else 'rolled-back'), phase
  assert (target/'user-extra.txt').exists() == (decision != 'recovery' and (decision == 'rollback' or not mac))
  results[decision] = phase
(out/'result.json').write_text(json.dumps(results, indent=2))
print(json.dumps(results))
