const fs = require('fs');
const p = 'C:/EInvGstAddon/src/EInvGstAddon.UI/Forms.cs';
let s = fs.readFileSync(p, 'utf8');
const marker = '    // =====================================================================================================\n    // Dashboard';
let i = s.indexOf(marker);
if (i < 0) { s = s.replace(/\r\n/g, '\n'); i = s.indexOf(marker); }
if (i < 0) { console.log('marker not found'); process.exit(1); }
s = s.slice(0, i) + fs.readFileSync('C:/EInvGstAddon/build/tmp/dash.cs', 'utf8');
fs.writeFileSync(p, s);
console.log('ok');
