// web/index.html의 실제 계산 코드를 실행해 Unity 코어 검증용 기준값(JSON)을 만든다.
// 사용: node data/golden.js  →  unity/Assets/Tests/EditMode/web-golden.json
const fs = require('fs'), vm = require('vm'), path = require('path');
const src = fs.readFileSync(path.join(__dirname, '../web/index.html'), 'utf8');
const cut = (a, b) => {
  const i = src.indexOf(a), j = src.indexOf(b, i);
  if (i < 0 || j < 0) throw new Error(`marker not found: ${a} .. ${b}`);
  return src.slice(i, j);
};

const code = [
  'Math.random = () => { if (!__q.length) throw new Error("random queue empty"); return __q.shift(); };',
  cut('const rnd =', '/* ---------- 사운드'),        // 유틸: rnd/pick/clamp/mulberry32/tileHash
  cut('const TYPES', '/* ---------- 몬스터 그리기'),  // 데이터·능력치·makeMon
  cut('const MW = 48', 'let time = 0;'),            // 맵 생성·지역명
  cut('function calcDamage', 'async function useMove'), // 데미지·적 AI
  `
  // startWild(브라우저 전투 호출 제외)와 동일한 야생 몬스터 결정 로직
  function wildFor(x, y, water) {
    const d = Math.hypot(x - VX, y - VY);
    const lv = clamp(2 + Math.floor(d / 3.4) + rnd(-1, 1), 2, 40);
    let pool = wildPool(d, water);
    let id = pick(pool);
    while (SP[id].ev && lv >= SP[id].ev[0]) id = SP[id].ev[1];
    return { id, lv };
  }
  const q = (...v) => { __q.length = 0; __q.push(...v); };
  ({
    rng: (() => { const r = mulberry32(20240921); return Array.from({ length: 8 }, () => r()); })(),
    map: genMap().map(row => row.map(t => t.toString(36)).join('')),
    areas: [[24, 18], [31, 18], [10, 18], [40, 18], [24, 30], [24, 3], [5, 5], [24, 25]].map(([x, y]) => ({ x, y, name: areaName(x, y) })),
    need: Array.from({ length: 60 }, (_, i) => need(i + 1)),
    stats: SP.map(s => ({ id: s.id, byLevel: [1, 5, 16, 30, 60].map(lv => { const m = { id: s.id, lv }; recalc(m); return { lv, hp: m.mhp, atk: m.atk, def: m.def, spd: m.spd }; }) })),
    makeMon: [[0, 5], [0, 14], [0, 27], [10, 5], [10, 18], [13, 30], [2, 40]].map(([id, lv]) => ({ id, lv, moves: makeMon(id, lv).moves })),
    damage: [
      // [공격자 종족, 레벨], [방어자 종족, 레벨], 기술, [크리티컬 굴림, 편차 굴림]
      [[0, 5], [4, 5], 'ember', [.5, 0]], [[0, 5], [4, 5], 'ember', [.5, .9999]], [[0, 5], [4, 5], 'ember', [.01, .5]],
      [[2, 12], [0, 12], 'bubble', [.5, .3]], [[8, 20], [0, 20], 'edge', [.5, .7]], [[6, 18], [8, 18], 'volt', [.5, .5]],
      [[10, 8], [8, 8], 'scratch', [.5, .5]], [[1, 30], [5, 30], 'blast', [.5, .99]], [[9, 40], [3, 40], 'edge', [.05, .0]],
      [[0, 5], [12, 3], 'tackle', [.5, .5]], [[15, 40], [9, 2], 'blast', [.5, .5]], [[3, 16], [1, 16], 'wave', [.5, .25]],
    ].map(([a, d, mv, rolls]) => {
      const A = makeMon(...a), D = makeMon(...d);
      q(...rolls);
      const r = calcDamage(A, D, MOVES[mv]);
      return { a, d, move: mv, rolls, dmg: r.dmg, eff: r.e, crit: r.crit };
    }),
    enemyMove: [
      [[0, 30], [4, 20], [.3]], [[0, 30], [4, 20], [.8, .5]], [[0, 30], [2, 20], [.7, 0]], [[10, 20], [8, 20], [.59]],
      [[6, 27], [3, 27], [.6, .99]], [[1, 27], [1, 27], [.1]],
    ].map(([e, t, rolls]) => {
      const E = makeMon(...e), Tm = makeMon(...t);
      q(...rolls);
      return { e, t, rolls, move: pickEnemyMove(E, Tm), moves: E.moves };
    }),
    wild: [
      [24, 21, [.5, .5]], [24, 21, [0, 0]], [24, 21, [.99, .99]], [30, 22, [.5, .2]], [34, 12, [.5, .9]], [44, 33, [.99, .3]],
      [40, 30, [.5, .1]], [40, 30, [.5, .95]], [12, 33, [0.4, .55]], [3, 3, [.3, .7]], [18, 22, [.9, .05]],
    ].map(([x, y, rolls]) => { q(...rolls); return { x, y, rolls, ...wildFor(x, y) }; }),
    // 물 타일에서의 조우: 물 타입만 나온다(풀숲 풀에서는 물 타입이 빠진다).
    wildWater: [
      [30, 10, [.5, .5]], [30, 10, [0, 0]], [30, 10, [.99, .99]], [44, 33, [.5, .2]], [8, 30, [.9, .6]], [24, 3, [.3, .99]],
    ].map(([x, y, rolls]) => { q(...rolls); return { x, y, rolls, water: true, ...wildFor(x, y, true) }; }),
    // 명세 공식: p = clamp(rate * (1 - 0.6 * hp/maxHp) + 0.08, 0.05, 0.95)
    catchP: [[0.5, 30, 30], [0.5, 15, 30], [0.5, 1, 30], [0.3, 30, 30], [0.25, 60, 60], [0.6, 5, 40], [0.6, 1, 200], [0.6, 200, 200]]
      .map(([rate, hp, mhp]) => ({ rate, hp, mhp, p: clamp(rate * (1 - .6 * (hp / mhp)) + .08, .05, .95) })),
    // 명세 공식: p = clamp(0.5 + (내 - 적)/(합) * 0.5 + 시도 * 0.15, 0.25, 1)
    flee: [[30, 30, 0], [40, 20, 0], [20, 40, 0], [20, 40, 1], [10, 60, 0], [10, 60, 3], [80, 10, 2]]
      .map(([p, e, n]) => ({ p, e, tries: n, chance: clamp(.5 + (p - e) / (p + e) * .5 + n * .15, .25, 1) })),
  })
  `,
].join('\n');

const golden = vm.runInNewContext(code, { __q: [] });
const out = path.join(__dirname, '../unity/Assets/Tests/EditMode/web-golden.json');
fs.mkdirSync(path.dirname(out), { recursive: true });
fs.writeFileSync(out, JSON.stringify(golden, null, 1), 'utf8');
console.log('golden written:', path.relative(process.cwd(), out),
  '| map', golden.map.length + 'x' + golden.map[0].length, '| damage', golden.damage.length, '| wild', golden.wild.length);
