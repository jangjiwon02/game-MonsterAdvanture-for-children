using System;
using System.Collections.Generic;

namespace MonsterAdventure.Core
{
    public readonly struct Vec2
    {
        public readonly double X, Y;
        public Vec2(double x, double y) { X = x; Y = y; }
        public static Vec2 operator +(Vec2 a, Vec2 b) => new Vec2(a.X + b.X, a.Y + b.Y);
        public static Vec2 operator -(Vec2 a, Vec2 b) => new Vec2(a.X - b.X, a.Y - b.Y);
        public static Vec2 operator *(Vec2 a, double s) => new Vec2(a.X * s, a.Y * s);
        public double Dot(Vec2 o) => X * o.X + Y * o.Y;
        public double Length => Math.Sqrt(X * X + Y * Y);
        public override string ToString() => $"({X:0.###}, {Y:0.###})";
    }

    public sealed class ThrowResult
    {
        /// <summary>애니메이션용 궤적 점(고정 dt 간격). 명중이면 착탄 지점에서 끝나고, 빗나가면 아래로 떨어질 때까지.</summary>
        public IReadOnlyList<Vec2> Path;
        public bool Hit;
        /// <summary>명중이면 목표 원에 처음 닿은 지점, 빗나갔으면 궤적의 마지막 점.</summary>
        public Vec2 Impact;
        /// <summary>궤적 전체에서 목표 중심에 가장 가까웠던 거리.</summary>
        public double ClosestDistance;
        /// <summary>0~1. 명중일 때 1 - 최근접거리/반지름(중심을 지날수록 1), 빗나가면 0.</summary>
        public double Quality;
        /// <summary>착탄(또는 마지막 점)까지의 비행 시간.</summary>
        public double FlightTime;
        /// <summary>"훌륭한 투척": 최근접거리가 반지름의 35% 이내.</summary>
        public bool Excellent;
    }

    /// <summary>
    /// 물리 기반 볼 투척 — 난수 없는 순수 수학. 2차원, y 가 위쪽 양수, 각도는 +x 축에서 반시계 방향 라디안.
    /// 위치는 고정 dt 마다 해석식(x0 + v·cosθ·t, y0 + v·sinθ·t - g·t²/2)으로 표본을 뽑으므로 적분 오차가 없고
    /// 같은 입력이면 항상 같은 결과다. 표본 사이 구간은 선분으로 보고 원과의 교차/최근접을 잡아서
    /// 공이 빠르거나 목표가 작아도 통과(터널링)하지 않는다. 단위는 자유(런타임은 480x320 가상 화면 픽셀).
    /// </summary>
    public static class BallisticThrow
    {
        public const double ExcellentRatio = 0.35;
        public const double MaxFlightTime = 30.0;
        public const double MinMultiplier = 1.0, MaxMultiplier = 1.5;

        public static Vec2 PositionAt(Vec2 start, double speed, double angle, double gravity, double t) =>
            new Vec2(start.X + speed * Math.Cos(angle) * t,
                     start.Y + speed * Math.Sin(angle) * t - 0.5 * gravity * t * t);

        public static ThrowResult Simulate(Vec2 start, double speed, double angle, double gravity,
                                           Vec2 targetCenter, double targetRadius, double dt = 1.0 / 60.0)
        {
            if (!(dt > 0)) throw new ArgumentOutOfRangeException(nameof(dt));
            if (!(targetRadius > 0)) throw new ArgumentOutOfRangeException(nameof(targetRadius));
            if (speed < 0) throw new ArgumentOutOfRangeException(nameof(speed));
            if (gravity < 0) throw new ArgumentOutOfRangeException(nameof(gravity));

            // 목표 원 아래로 충분히 떨어지면(그리고 내려가는 중이면) 비행 종료.
            double floorY = Math.Min(start.Y, targetCenter.Y - targetRadius);
            var path = new List<Vec2> { start };
            double best = double.MaxValue;
            bool hit = false;
            Vec2 impact = start;
            int hitIndex = 0;        // path 안에서 착탄 점의 위치
            double hitTime = 0;

            var (d0, _) = ClosestOnSegment(start, start, targetCenter);
            best = d0;
            if (d0 <= targetRadius) { hit = true; impact = start; hitIndex = 0; hitTime = 0; }

            var prev = start;
            int maxSteps = (int)Math.Ceiling(MaxFlightTime / dt);
            int steps = 0;
            for (int i = 1; i <= maxSteps; i++)
            {
                double t = i * dt;
                var p = PositionAt(start, speed, angle, gravity, t);
                path.Add(p);
                steps = i;

                var (d, _) = ClosestOnSegment(prev, p, targetCenter);
                if (d < best) best = d;
                if (!hit && d <= targetRadius)
                {
                    hit = true;
                    double s = EntryFraction(prev, p, targetCenter, targetRadius);
                    impact = prev + (p - prev) * s;
                    hitIndex = i;             // path[i] 를 impact 로 바꿔서 쓴다
                    hitTime = (i - 1 + s) * dt;
                }

                double vy = speed * Math.Sin(angle) - gravity * t;
                prev = p;
                if (vy < 0 && p.Y < floorY) break;
            }

            var result = new ThrowResult { Hit = hit, ClosestDistance = best };
            if (hit)
            {
                var trimmed = new List<Vec2>(path.GetRange(0, hitIndex));
                trimmed.Add(impact);
                result.Path = trimmed;
                result.Impact = impact;
                result.FlightTime = hitTime;
                result.Quality = Clamp01(1.0 - best / targetRadius);
                result.Excellent = best <= targetRadius * ExcellentRatio;
            }
            else
            {
                result.Path = path;
                result.Impact = path[path.Count - 1];
                result.FlightTime = steps * dt;
                result.Quality = 0.0;
            }
            return result;
        }

        /// <summary>
        /// 주어진 속도로 start 에서 target 을 맞히는 발사 각도(라디안). 기본은 낮은(평평한) 호, highArc 면 높은 호.
        /// 속도가 모자라 닿지 못하면 null. 목표가 왼쪽이면 각도는 90°~180° 쪽으로 나온다.
        /// </summary>
        public static double? SolveAngle(Vec2 start, double speed, double gravity, Vec2 target, bool highArc = false)
        {
            if (!(speed > 0) || gravity < 0) return null;
            double dx = target.X - start.X, dy = target.Y - start.Y;
            if (gravity == 0) return dx == 0 && dy == 0 ? (double?)0.0 : Math.Atan2(dy, dx);

            if (dx == 0)
            {
                // 곧장 위로 쏴서 높이 v²/2g 까지 올라가면 닿는다(아래에 있는 목표도 올라갔다 내려오며 지난다).
                return speed * speed / (2 * gravity) >= dy ? (double?)(Math.PI / 2) : null;
            }

            double ax = Math.Abs(dx);
            double a = gravity * ax * ax / (2 * speed * speed);
            double disc = ax * ax - 4 * a * (a + dy);
            if (disc < 0) return null;
            double root = Math.Sqrt(disc);
            double tan = (ax + (highArc ? root : -root)) / (2 * a);
            double theta = Math.Atan(tan);
            return dx > 0 ? theta : Math.PI - theta;
        }

        /// <summary>투척 품질(0~1) → BallAction.throwQuality 배수(1.0~1.5).</summary>
        public static double ThrowQualityMultiplier(double quality) =>
            MinMultiplier + (MaxMultiplier - MinMultiplier) * Clamp01(quality);

        static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

        static (double dist, Vec2 point) ClosestOnSegment(Vec2 a, Vec2 b, Vec2 c)
        {
            var d = b - a;
            double len2 = d.Dot(d);
            double s = len2 <= 0 ? 0 : Clamp01((c - a).Dot(d) / len2);
            var pt = a + d * s;
            return ((c - pt).Length, pt);
        }

        /// <summary>선분 a→b 가 원에 처음 닿는 지점의 비율(0~1).</summary>
        static double EntryFraction(Vec2 a, Vec2 b, Vec2 c, double r)
        {
            var d = b - a; var f = a - c;
            double qa = d.Dot(d);
            if (qa <= 0) return 0;
            double qb = 2 * f.Dot(d), qc = f.Dot(f) - r * r;
            if (qc <= 0) return 0;                   // 시작점이 이미 원 안
            double disc = qb * qb - 4 * qa * qc;
            double s = (-qb - Math.Sqrt(Math.Max(0, disc))) / (2 * qa);
            return Clamp01(s);
        }
    }
}
