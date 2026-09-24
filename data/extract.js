// web/index.html의 게임 데이터를 엔진 중립 JSON으로 추출한다.
const fs = require('fs'), vm = require('vm');
const src = fs.readFileSync(__dirname + '/../web/index.html', 'utf8');
const a = src.indexOf('const TYPES'), b = src.indexOf('const need =');
const code = src.slice(a, b) + '\n;({TYPES,CHART,MOVES,LEARN,SP,BASE_IDS})';
const d = vm.runInNewContext(code);
const species = d.SP.map(s => ({
  id: s.id, name: s.n, type: s.t, look: s.f, stage: s.st,
  color: s.c, belly: s.c2,
  baseStats: { hp: s.b[0], atk: s.b[1], def: s.b[2], spd: s.b[3] },
  evolve: s.ev ? { level: s.ev[0], to: s.ev[1] } : null,
  catchRate: s.rate, baseExp: s.xp,
  learnset: s.learn.map(([level, move]) => ({ level, move })),
}));
const moves = Object.entries(d.MOVES).map(([id, m]) => ({ id, name: m.n, type: m.t, power: m.p, accuracy: m.a }));
const out = {
  types: Object.entries(d.TYPES).map(([id, t]) => ({ id, name: t.n, color: t.c })),
  typeChart: d.CHART,           // 공격타입 -> 방어타입 -> 배율 (없으면 1)
  moves, species,
};
fs.writeFileSync(__dirname + '/game-data.json', JSON.stringify(out, null, 2), 'utf8');
console.log('types', out.types.length, 'moves', moves.length, 'species', species.length);
