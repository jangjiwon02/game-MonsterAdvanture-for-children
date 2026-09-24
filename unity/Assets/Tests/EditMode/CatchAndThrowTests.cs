using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MonsterAdventure.Core;
using NUnit.Framework;
using UnityEngine;

namespace MonsterAdventure.Tests
{
    /// <summary>흔들림 3회 포획 판정 · 볼 투척 물리 · 세션의 BallAction/CaptureMode.</summary>
    public class CatchAndThrowTests
    {
        GameData _data;

        [OneTimeSetUp]
        public void Load() =>
            _data = GameData.Parse(File.ReadAllText(Path.Combine(Application.dataPath, "Resources/game-data.json")));

        static List<BattleEvent> Play(BattleSession session, BattleAction action)
        {
            var events = new List<BattleEvent>();
            foreach (var e in session.ResolveTurn(action))
            {
                events.Add(e);
                if (e.Kind == BattleEventKind.ReplacementNeeded) session.ChooseReplacement(1);
            }
            return events;
        }

        static BattleEventKind[] Kinds(IEnumerable<BattleEvent> events) => events.Select(e => e.Kind).ToArray();

        PlayerState State(int starter = 0)
        {
            var s = PlayerState.NewGame(_data, starter);
            s.Party[0] = Monster.Create(_data, starter, 5);
            return s;
        }

        /* ------------------------------ CatchMath ------------------------------ */

        [Test]
        public void CatchValue_FollowsTheClassicFormula()
        {
            // ((3*100 - 2*100) * 0.6 * 1) / (3*100) = 0.2 (만피), HP 0 이면 rate 그대로
            Assert.AreEqual(0.2, CatchMath.CatchValue(0.6, 100, 100), 1e-12);
            Assert.AreEqual(0.6, CatchMath.CatchValue(0.6, 0, 100), 1e-12);
            Assert.AreEqual(0.4, CatchMath.CatchValue(0.6, 100, 100, ballMultiplier: 2.0), 1e-12);
            Assert.AreEqual(0.3, CatchMath.CatchValue(0.6, 100, 100, statusMultiplier: 1.5), 1e-12);
        }

        [Test]
        public void Probability_LowerHpIsEasier_ClampedAndMonotonicInBallAndStatus()
        {
            Assert.Greater(CatchMath.Probability(0.5, 1, 30), CatchMath.Probability(0.5, 30, 30));
            Assert.AreEqual(0.95, CatchMath.Probability(0.9, 1, 100, 3.0, 2.0), 1e-12, "상한");
            Assert.AreEqual(0.08, CatchMath.Probability(0.0, 100, 100), 1e-12, "rate 0 → 보너스만");
            Assert.Greater(CatchMath.Probability(0.3, 50, 100, 1.5), CatchMath.Probability(0.3, 50, 100, 1.0));
            Assert.Greater(CatchMath.Probability(0.3, 50, 100, 1.0, 2.0), CatchMath.Probability(0.3, 50, 100, 1.0, 1.0));
            Assert.Throws<ArgumentOutOfRangeException>(() => CatchMath.Probability(0.3, 5, 0));
        }

        [Test]
        public void Probability_StaysCloseToTheLegacyFormula_ForEverySpecies()
        {
            foreach (var sp in _data.Species)
                for (int hp = 1; hp <= 100; hp += 3)
                {
                    double legacy = Capture.Probability(sp.CatchRate, hp, 100);
                    double now = CatchMath.Probability(sp.CatchRate, hp, 100);
                    Assert.AreEqual(legacy, now, 0.1 * sp.CatchRate + 1e-9, $"{sp.Name} hp={hp}");
                }
        }

        [Test]
        public void RollShakes_AllThreePass_Caught()
        {
            var rng = new ScriptedRng(0.1, 0.1, 0.1);
            var r = CatchMath.RollShakes(0.512, rng);          // 각 검사 통과 확률 0.8
            Assert.IsTrue(r.Caught);
            Assert.AreEqual(3, r.Shakes);
            Assert.AreEqual(0.512, r.Probability, 1e-12);
            Assert.AreEqual(0, rng.Remaining);
        }

        [Test]
        public void RollShakes_FailureIndexIsTheShakeCount_AndStopsRolling()
        {
            var first = new ScriptedRng(0.9, 0.0, 0.0);
            var r0 = CatchMath.RollShakes(0.512, first);
            Assert.IsFalse(r0.Caught); Assert.AreEqual(0, r0.Shakes); Assert.AreEqual(2, first.Remaining);

            var second = new ScriptedRng(0.1, 0.9, 0.0);
            var r1 = CatchMath.RollShakes(0.512, second);
            Assert.IsFalse(r1.Caught); Assert.AreEqual(1, r1.Shakes); Assert.AreEqual(1, second.Remaining);

            var third = new ScriptedRng(0.1, 0.1, 0.9);
            var r2 = CatchMath.RollShakes(0.512, third);
            Assert.IsFalse(r2.Caught); Assert.AreEqual(2, r2.Shakes); Assert.AreEqual(0, third.Remaining);
        }

        [Test]
        public void RollShakes_OverallSuccessRateMatchesTheProbability()
        {
            // 결정적 난수원(mulberry32)으로 표본을 뽑아 전체 확률이 P 와 맞는지 본다.
            var rng = new Mulberry32(12345);
            int caught = 0, n = 20000;
            for (int i = 0; i < n; i++) if (CatchMath.RollShakes(0.4, rng).Caught) caught++;
            Assert.AreEqual(0.4, caught / (double)n, 0.02);
        }

        [Test]
        public void Attempt_ThrowQuality_RaisesTheProbability()
        {
            var target = Monster.Create(_data, 6, 5);
            var plain = CatchMath.Attempt(_data, target, new ScriptedRng(0.0, 0.0, 0.0));
            var great = CatchMath.Attempt(_data, target, new ScriptedRng(0.0, 0.0, 0.0), throwQuality: 1.5);
            Assert.Greater(great.Probability, plain.Probability);
            Assert.IsTrue(plain.Caught);
        }

        /* ------------------------------ 세션 통합 ------------------------------ */

        [Test]
        public void Session_DefaultCaptureModeIsLegacy_AndUnchanged()
        {
            var state = State();
            var session = new BattleSession(_data, state, Monster.Create(_data, 6, 5), new ScriptedRng(0.1));
            Assert.AreEqual(CaptureMode.Legacy, session.CaptureMode);
            var events = Play(session, new BallAction());
            CollectionAssert.AreEqual(new[] { BattleEventKind.BallThrown, BattleEventKind.CatchAttempt, BattleEventKind.Caught }, Kinds(events));
        }

        [Test]
        public void Session_ThreeShake_CatchesWhenAllChecksPass()
        {
            var state = State();
            var rng = new ScriptedRng(0.0, 0.0, 0.0);
            var session = new BattleSession(_data, state, Monster.Create(_data, 6, 5), rng) { CaptureMode = CaptureMode.ThreeShake };

            var events = Play(session, new BallAction());

            CollectionAssert.AreEqual(new[] { BattleEventKind.BallThrown, BattleEventKind.CatchAttempt, BattleEventKind.Caught }, Kinds(events));
            Assert.AreEqual(3, events[1].Catch.Shakes);
            Assert.AreEqual(BattleOutcome.Caught, session.Outcome);
            Assert.AreEqual(4, state.Balls);
            Assert.AreEqual(2, state.Party.Count);
            Assert.AreEqual(0, rng.Remaining);
        }

        [Test]
        public void Session_ThreeShake_FailureShakesThenEnemyAttacks()
        {
            var state = State(2);                                          // 물방울이
            var rng = new ScriptedRng(0.0, 0.999,                          // 첫 검사 통과, 둘째 실패 → Shakes 1
                                      0.0, 0.0, 0.5, 0.5);                 // 적: 최고점 기술, 명중, 급소 아님, 편차
            var session = new BattleSession(_data, state, Monster.Create(_data, 6, 5), rng) { CaptureMode = CaptureMode.ThreeShake };

            var events = Play(session, new BallAction());

            CollectionAssert.AreEqual(new[]
            {
                BattleEventKind.BallThrown, BattleEventKind.CatchAttempt, BattleEventKind.MoveUsed, BattleEventKind.Damage,
            }, Kinds(events));
            Assert.IsFalse(events[1].Catch.Caught);
            Assert.AreEqual(1, events[1].Catch.Shakes);
            Assert.AreEqual(BattleOutcome.Ongoing, session.Outcome);
            Assert.AreEqual(0, rng.Remaining);
        }

        [Test]
        public void Session_ThreeShake_UsesBallAndThrowQuality()
        {
            double P(double quality, double ball)
            {
                var session = new BattleSession(_data, State(), Monster.Create(_data, 6, 5), new ScriptedRng(0.0, 0.0, 0.0))
                {
                    CaptureMode = CaptureMode.ThreeShake, BallMultiplier = ball,
                };
                return Play(session, new BallAction(true, quality))[1].Catch.Probability;
            }
            double plain = P(1.0, 1.0);
            Assert.Greater(P(1.5, 1.0), plain);
            Assert.Greater(P(1.0, 2.0), plain);
            Assert.AreEqual(P(1.5, 1.0), P(1.0, 1.5), 1e-12, "품질과 볼 계수는 곱으로 합쳐진다");
        }

        [Test]
        public void Session_BallMiss_ConsumesTheBallOnly_ThenEnemyAttacks()
        {
            var state = State(2);
            var rng = new ScriptedRng(0.0, 0.0, 0.5, 0.5);                 // 포획 판정 난수 없음 — 적 기술·명중·급소·편차뿐
            var session = new BattleSession(_data, state, Monster.Create(_data, 6, 5), rng);

            var events = Play(session, new BallAction(hit: false));

            CollectionAssert.AreEqual(new[]
            {
                BattleEventKind.BallThrown, BattleEventKind.BallMissed, BattleEventKind.MoveUsed, BattleEventKind.Damage,
            }, Kinds(events));
            Assert.AreEqual(4, state.Balls);
            Assert.AreEqual(1, state.Party.Count);
            Assert.AreEqual(BattleOutcome.Ongoing, session.Outcome);
            Assert.AreEqual(0, rng.Remaining);
        }

        [Test]
        public void Session_BallMiss_WithNoBalls_IsRejected()
        {
            var state = State();
            state.Balls = 0;
            var session = new BattleSession(_data, state, Monster.Create(_data, 6, 5), new ScriptedRng());
            Assert.Throws<InvalidOperationException>(() => session.ResolveTurn(new BallAction(false)).ToList());
        }

        /* ------------------------------ BallisticThrow ------------------------------ */

        static readonly Vec2 Origin = new Vec2(60, 40);

        [Test]
        public void SolveAngle_ThenSimulate_HitsDeadCenter_WithExcellentQuality()
        {
            var target = new Vec2(380, 190);
            const double v = 500, g = 400, r = 30;
            double? angle = BallisticThrow.SolveAngle(Origin, v, g, target);
            Assert.IsNotNull(angle);

            var res = BallisticThrow.Simulate(Origin, v, angle.Value, g, target, r);

            Assert.IsTrue(res.Hit);
            Assert.IsTrue(res.Excellent);
            Assert.Greater(res.Quality, 0.9);
            Assert.Less(res.ClosestDistance, 0.1 * r);
            Assert.AreEqual(res.Impact.X, res.Path[res.Path.Count - 1].X, 1e-9, "경로는 착탄 지점에서 끝난다");
            Assert.LessOrEqual((res.Impact - target).Length, r + 1e-9);
            Assert.AreEqual(1.5, BallisticThrow.ThrowQualityMultiplier(1.0));
        }

        [Test]
        public void HighArc_AlsoHits_AndIsSteeperThanTheLowArc()
        {
            var target = new Vec2(380, 190);
            double low = BallisticThrow.SolveAngle(Origin, 500, 400, target).Value;
            double high = BallisticThrow.SolveAngle(Origin, 500, 400, target, highArc: true).Value;
            Assert.Greater(high, low);
            Assert.IsTrue(BallisticThrow.Simulate(Origin, 500, high, 400, target, 30).Hit);
            Assert.Greater(BallisticThrow.Simulate(Origin, 500, high, 400, target, 30).Quality, 0.9);
        }

        [Test]
        public void Graze_IsAHitWithLowQuality()
        {
            // 중력 0 의 수평 직선: 목표 중심이 경로에서 9 만큼 위(반지름 10) → 스치듯 명중, 품질 0.1
            var res = BallisticThrow.Simulate(new Vec2(0, 0), 100, 0, 0, new Vec2(100, 9), 10, 0.01);
            Assert.IsTrue(res.Hit);
            Assert.IsFalse(res.Excellent);
            Assert.AreEqual(9.0, res.ClosestDistance, 1e-9);
            Assert.AreEqual(0.1, res.Quality, 1e-9);
            Assert.AreEqual(1.05, BallisticThrow.ThrowQualityMultiplier(res.Quality), 1e-9);
            Assert.Less(res.Impact.X, 100, "원의 테두리에서 먼저 닿는다");
        }

        [Test]
        public void Miss_HasNoQuality_AndFallsPastTheTarget()
        {
            var res = BallisticThrow.Simulate(new Vec2(0, 0), 100, 0, 0, new Vec2(100, 15), 10, 0.01);
            Assert.IsFalse(res.Hit);
            Assert.AreEqual(0.0, res.Quality);
            Assert.AreEqual(15.0, res.ClosestDistance, 1e-9);
            Assert.AreEqual(1.0, BallisticThrow.ThrowQualityMultiplier(res.Quality));

            // 중력이 있는 경우: 너무 세게 던지면 위로 넘어가고 결국 떨어져서 끝난다.
            var lob = BallisticThrow.Simulate(Origin, 700, 0.9, 400, new Vec2(380, 190), 30);
            Assert.IsFalse(lob.Hit);
            Assert.Less(lob.Path[lob.Path.Count - 1].Y, Origin.Y, "출발 높이 아래로 떨어질 때까지 그린다");
        }

        [Test]
        public void SolveAngle_ReturnsNull_WhenOutOfReach()
        {
            Assert.IsNull(BallisticThrow.SolveAngle(Origin, 100, 400, new Vec2(380, 190)), "속도 부족");
            Assert.IsNull(BallisticThrow.SolveAngle(Origin, 0, 400, new Vec2(380, 190)));
            Assert.IsNull(BallisticThrow.SolveAngle(new Vec2(0, 0), 50, 400, new Vec2(0, 1000)), "수직으로도 못 닿음");
            Assert.AreEqual(Math.PI / 2, BallisticThrow.SolveAngle(new Vec2(0, 0), 100, 100, new Vec2(0, 20)).Value, 1e-12);
        }

        [Test]
        public void ComplementaryAngles_LandAtTheSameRange_OnLevelGround()
        {
            const double v = 300, g = 400;
            var start = new Vec2(0, 0);
            double range = v * v * Math.Sin(2 * 0.5) / g;
            var target = new Vec2(range, 0);
            foreach (double angle in new[] { 0.5, Math.PI / 2 - 0.5 })
            {
                var res = BallisticThrow.Simulate(start, v, angle, g, target, 5);
                Assert.IsTrue(res.Hit, $"angle {angle}");
                Assert.Greater(res.Quality, 0.8);
            }
        }

        [Test]
        public void Mirroring_LeftwardThrow_GivesTheMirrorImage()
        {
            var target = new Vec2(380, 190);
            double right = BallisticThrow.SolveAngle(Origin, 500, 400, target).Value;
            var mirroredTarget = new Vec2(2 * Origin.X - target.X, target.Y);
            double left = BallisticThrow.SolveAngle(Origin, 500, 400, mirroredTarget).Value;
            Assert.AreEqual(Math.PI - right, left, 1e-12);

            var a = BallisticThrow.Simulate(Origin, 500, right, 400, target, 30);
            var b = BallisticThrow.Simulate(Origin, 500, left, 400, mirroredTarget, 30);
            Assert.AreEqual(a.Path.Count, b.Path.Count);
            Assert.AreEqual(a.Quality, b.Quality, 1e-9);
            for (int i = 0; i < a.Path.Count; i++)
            {
                Assert.AreEqual(2 * Origin.X - a.Path[i].X, b.Path[i].X, 1e-9);
                Assert.AreEqual(a.Path[i].Y, b.Path[i].Y, 1e-9);
            }
        }

        [Test]
        public void Simulate_IsDeterministic()
        {
            var t = new Vec2(300, 120);
            var a = BallisticThrow.Simulate(Origin, 450, 0.8, 400, t, 25);
            var b = BallisticThrow.Simulate(Origin, 450, 0.8, 400, t, 25);
            Assert.AreEqual(a.Hit, b.Hit);
            Assert.AreEqual(a.Quality, b.Quality);
            Assert.AreEqual(a.Path.Count, b.Path.Count);
            for (int i = 0; i < a.Path.Count; i++)
            {
                Assert.AreEqual(a.Path[i].X, b.Path[i].X);
                Assert.AreEqual(a.Path[i].Y, b.Path[i].Y);
            }
        }

        [Test]
        public void FastBall_DoesNotTunnelThroughASmallTarget()
        {
            // 한 프레임에 목표 지름보다 훨씬 멀리 가도(속도 6000, dt 1/60 → 100 씩) 선분 검사로 잡아낸다.
            var res = BallisticThrow.Simulate(new Vec2(0, 0), 6000, 0, 0, new Vec2(250, 0), 4, 1.0 / 60);
            Assert.IsTrue(res.Hit);
            Assert.Greater(res.Quality, 0.99);
        }

        [Test]
        public void Simulate_RejectsBadArguments()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => BallisticThrow.Simulate(Origin, 100, 0.5, 400, Vec2Zero, 10, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => BallisticThrow.Simulate(Origin, 100, 0.5, 400, Vec2Zero, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => BallisticThrow.Simulate(Origin, -1, 0.5, 400, Vec2Zero, 10));
        }

        static readonly Vec2 Vec2Zero = new Vec2(0, 0);

        [Test]
        public void ThrowQualityMultiplier_MapsZeroToOneOntoOneToOnePointFive()
        {
            Assert.AreEqual(1.0, BallisticThrow.ThrowQualityMultiplier(0.0));
            Assert.AreEqual(1.25, BallisticThrow.ThrowQualityMultiplier(0.5), 1e-12);
            Assert.AreEqual(1.5, BallisticThrow.ThrowQualityMultiplier(1.0));
            Assert.AreEqual(1.5, BallisticThrow.ThrowQualityMultiplier(7.0), "범위 밖은 잘라낸다");
            Assert.AreEqual(1.0, BallisticThrow.ThrowQualityMultiplier(-3.0));
        }
    }
}
