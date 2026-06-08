// 워크플로우 JS 정적 문법검사 — async 함수 본문으로 감싸 파싱(실행 안 함)해 SyntaxError 만 검출.
// 워크플로우 본문은 런타임이 async 컨텍스트로 실행하므로 top-level await/return 이 정상 —
// `node --check`(파일을 모듈/스크립트로 파싱)는 이를 오탐한다. AsyncFunction 생성자는 본문을
// async 함수로 파싱하므로 await/return 둘 다 합법이고, ESM `export` 만 제거하면 워크플로우 포맷과 일치.
// 파싱만 하고 실행하지 않으므로 부작용 0. exit 0=OK, 1=문법오류(메시지 stderr), 2=사용오류.
const fs = require('fs');
const file = process.argv[2];
if (!file) {
  console.error('usage: check-workflow-syntax.cjs <file.js>');
  process.exit(2);
}
let code;
try {
  code = fs.readFileSync(file, 'utf8');
} catch (e) {
  console.error('read error: ' + e.message);
  process.exit(2);
}
const AsyncFunction = Object.getPrototypeOf(async function () {}).constructor;
const stripped = code.replace(/^\s*export\s+/gm, ''); // ESM export → async 본문에서 합법화
try {
  new AsyncFunction(stripped); // 파싱만 — 실행하지 않음
  process.exit(0);
} catch (e) {
  console.error(e.message);
  process.exit(1);
}
