// Cross-platform contract test for the SAME JS shipped by Windows and macOS.
// This runs on V8; macOS --self-test independently exercises JavaScriptCore.
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const code = fs.readFileSync(path.join(__dirname, '../../installer/package-quota.js'), 'utf8');
const now = Date.parse('2026-09-10T08:00:00Z');
class Clock extends Date { static now() { return now; } }
const script = vm.runInNewContext(code, { Date: Clock }, { timeout: 1000 });
function fixture(plan = 'pro') {
  return { success: true, data: { plan, updated_at: new Date(now).toISOString(), status: 'available',
    site_balance: { status: 'available', remaining: 0 },
    windows: [18000, 604800].map((seconds) => ({ name: String(seconds), window_seconds: seconds,
      remaining_percent: 72, reset_at: new Date(now + 86400000).toISOString() })),
    estimation: { windows: [{ name: '604800', status: 'ready', usd_per_percentage_point: 0.5 }] }
  }};
}
function rows(value) { return JSON.parse(JSON.stringify(script.extractor(value))); }
const pro = rows(fixture());
assert.equal(pro.length, 3);
assert.equal(pro.filter(x => x.total === 100).length, 1);
assert.equal(pro[0].remaining, 0); assert.equal(pro[0].isValid, true);
assert.equal(pro[2].remaining, 0); assert.equal(pro[2].isValid, true);
assert.equal(rows(fixture('plus')).length, 4);
const stale = fixture(); stale.data.updated_at = new Date(now - 360001).toISOString();
assert.equal(rows(stale)[1].isValid, false); assert.equal(rows(stale)[2].isValid, false);
const reset = fixture(); reset.data.windows[1].reset_at = new Date(now).toISOString();
assert.equal(rows(reset)[1].isValid, false);
const missing = fixture(); delete missing.data.estimation;
assert.equal(rows(missing)[2].isValid, false);
assert.equal(rows({ success: false }).isValid, false);
console.log('PASS: shared template PRO/dual quota, zero balance/estimate, stale/reset/missing estimate, failure');
