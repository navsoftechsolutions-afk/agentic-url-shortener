#!/usr/bin/env python3
"""Reproduce the public API demo without printing credentials or invoking a shell."""
import argparse
import json
import os
from pathlib import Path
import urllib.error
import urllib.request

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--base', default='http://localhost:5080')
parser.add_argument('--keys', type=Path, default=Path(__file__).resolve().parents[1] / 'src/AgenticUrlShortener/data/access-keys.json')
parser.add_argument('--failure-demos', action='store_true', help='Requires ENABLE_FAILURE_INJECTION=true on the server')
parser.add_argument('--output', type=Path, help='Save a credential-free evidence summary')
args = parser.parse_args()
keys = {'operator': os.getenv('OPERATOR_API_KEY'), 'reviewer': os.getenv('REVIEWER_API_KEY')}
if not all(keys.values()):
    keys = json.loads(args.keys.read_text())

class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *unused):
        return None
opener = urllib.request.build_opener(NoRedirect)

def request(path, data=None, role='operator', method=None, expected=200):
    headers = {'Content-Type': 'application/json'}
    if role:
        headers['X-Api-Key'] = keys[role]
    req = urllib.request.Request(args.base.rstrip('/') + path,
        data=json.dumps(data).encode() if data is not None else None, headers=headers, method=method)
    try:
        response = opener.open(req, timeout=180)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        raw = response.read().decode()
        assert response.status == expected, f'{path}: expected {expected}, got {response.status}: {raw}'
        return json.loads(raw) if raw and 'application/json' in response.headers.get('Content-Type', '') else raw

def create(requirement, scenario='greenfield', **flags):
    return request('/api/workflows', {'requirement': requirement, 'scenario': scenario, **flags}, expected=201)

def decide(w, approved):
    return request(f"/api/workflows/{w['id']}/approval", {
        'approved': approved, 'revision': w['revision'], 'evidenceHash': w['reviewDigest'],
        'reason': 'Reviewed generated source, executed test evidence, and documented risks.'
    }, role='reviewer')

def summarize(w):
    summary = {field: w[field] for field in ['id', 'scenario', 'status', 'revision', 'retries', 'rollbacks', 'fallbacks', 'reviewDigest']}
    summary['validation'] = json.loads(w['artifacts']['tests']) if 'tests' in w['artifacts'] else None
    summary['auditEvents'] = sorted({event['event'] for event in w['audit']})
    return summary

assert request('/', role=None)['version'] == '2.0'
request('/docs', role=None)
request('/openapi.json', role=None)
request('/api/workflows', role=None, expected=401)
link = request('/api/urls', {'url': 'https://example.com', 'expiresInDays': 30}, expected=201)
code = link['code']
request('/' + code, role=None, expected=302)
assert request(f'/api/urls/{code}/analytics')['clicks'] == 1
request('/api/urls/' + code, method='DELETE', expected=204)
request('/' + code, role=None, expected=404)
print('PASS authentication, docs, API schema, URL redirect/analytics/disable')

results = []
w = create('Build HTTPS-only URL shortener with expiration and click analytics', simulateTestFailure=args.failure_demos)
assert w['status'] == 'WaitingApproval', w['status']
assert w['nodes']['release']['status'] == 0
assert 'release' not in w['artifacts']
evidence = json.loads(w['artifacts']['tests'])
assert evidence['Runner'] == 'dotnet test' and evidence['Passed'] == 3
if args.failure_demos:
    assert w['retries'] == 1 and w['nodes']['tests']['attempts'] == 2
assert 'HttpsOnly = true' in request(f"/api/workflows/{w['id']}/artifacts/app/FeaturePolicy.cs", role='reviewer')
audit = request(f"/api/workflows/{w['id']}/audit")
assert audit['integrity'] == 'verified'
request(f"/api/workflows/{w['id']}/approval", {'approved': True, 'revision': w['revision'],
    'evidenceHash': w['reviewDigest'], 'reason': 'Must be forbidden for creator'}, expected=403)
w = decide(w, True)
assert w['status'] == 'Completed' and w['approvedBy'] == 'reviewer'
results.append(summarize(w))
print('PASS greenfield: real generated application and tests, approval gate, authenticated completion')

w = create('Add click analytics to existing links', 'brownfield')
assert 'migration' not in w['nodes']
old = w
w = request(f"/api/workflows/{w['id']}/replan", {'requirement': 'Add expiration and click analytics to existing links', 'revision': w['revision']})
assert w['revision'] == 2 and 'migration' in w['nodes']
assert w['history'][0]['reviewDigest'] == old['reviewDigest']
request(f"/api/workflows/{w['id']}/approval", {'approved': True, 'revision': old['revision'],
    'evidenceHash': old['reviewDigest'], 'reason': 'Stale approval must fail'}, role='reviewer', expected=409)
w = decide(w, True)
assert w['status'] == 'Completed'
results.append(summarize(w))
print('PASS brownfield: changed graph, compatibility check, quarantined revision, stale approval blocked')

w = create('Make short links secure', 'ambiguous')
assert w['status'] == 'WaitingClarification' and w['nodes']['implementation']['status'] == 0
w = request(f"/api/workflows/{w['id']}/replan", {'requirement': 'Require HTTPS-only links and click analytics', 'revision': w['revision']})
assert w['status'] == 'WaitingApproval'
w = decide(w, False)
assert w['status'] == 'Rejected' and 'release' not in w['artifacts']
results.append(summarize(w))
print('PASS ambiguous: clarification gate, explicit acceptance criteria, rejection and quarantine')

if args.failure_demos:
    w = create('Build click analytics', failureMode='persistent-tests')
    assert w['status'] == 'SafeStopped' and w['nodes']['tests']['attempts'] == 3
    assert w['fallbacks'] == 0 and w['nodes']['release']['status'] == 0
    results.append(summarize(w))
    w = request(f"/api/workflows/{w['id']}/replan", {'requirement': 'Build HTTPS-only links with click analytics', 'revision': w['revision']})
    assert w['status'] == 'WaitingApproval' and w['failureMode'] == 'none'
    assert decide(w, False)['status'] == 'Rejected'
    print('PASS persistent failure: bounded retries, safe-stop, quarantine, recovery through explicit replan')
    w = create('Build click analytics', failureMode='runner-unavailable')
    assert w['status'] == 'WaitingApproval' and w['fallbacks'] == 1 and w['retries'] == 2
    assert json.loads(w['artifacts']['tests'])['Runner'] == 'package-free shared contract verifier'
    results.append(summarize(decide(w, False)))
    print('PASS runner outage: equivalent contract fallback executed and recorded')

metrics = request('/api/workflows/metrics')
assert metrics['completed'] >= 2
if args.failure_demos:
    assert metrics['recoveredIncidents'] >= 2 and metrics['meanTimeToRecoveryMs'] > 0
print('PASS reliability metrics and actual recovery intervals')
report = {'serviceVersion': '2.0', 'failureDemonstrations': args.failure_demos, 'workflows': results, 'metrics': metrics}
if args.output:
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2) + '\n')
    print('Evidence summary saved:', args.output)
