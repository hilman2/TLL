using System;
using System.Collections.Generic;
using TLL.Core.Control;

namespace TLL.Core.Tests.Control
{
    /// <summary>
    /// Drives one controller step by step and records what it shows.
    /// </summary>
    internal sealed class ControllerHarness
    {
        public ControllerState State;
        public ControllerConfig Config;
        public readonly PhaseData[] Phases;
        public readonly List<Record> Trace = new List<Record>();

        public struct Record
        {
            public long Step;
            public Stage Stage;
            public int Phase;
            public int Next;
            public int StageSteps;
            public bool Walk;
        }

        public ControllerHarness(ControllerConfig config, params PhaseData[] phases)
        {
            Config = config;
            Phases = phases;
        }

        /// <summary>
        /// Runs the controller from <paramref name="from"/> for <paramref name="steps"/> steps.
        /// <paramref name="sense"/> is called before every step to set the sensor readings.
        /// Called as <c>sense(step, phases)</c>.
        /// </summary>
        public void Run(long from, int steps, Action<long, PhaseData[]> sense = null)
        {
            var access = new PhaseArray(Phases);
            for (long step = from; step < from + steps; step++)
            {
                sense?.Invoke(step, Phases);
                SignalController.Step(ref State, in Config, ref access, step);
                Trace.Add(new Record { Step = step, Stage = State.Stage, Phase = State.Phase, Next = State.Next, StageSteps = State.StageSteps, Walk = State.Walk });
            }
        }

        /// <summary>Steps at which a green began, with the phase that got it.</summary>
        public List<(long step, int phase)> GreenStarts()
        {
            var starts = new List<(long, int)>();
            for (int i = 0; i < Trace.Count; i++)
            {
                bool green = Trace[i].Stage == Stage.Green;
                bool wasGreenSame = i > 0 && Trace[i - 1].Stage == Stage.Green && Trace[i - 1].Phase == Trace[i].Phase;
                if (green && !wasGreenSame)
                    starts.Add((Trace[i].Step, Trace[i].Phase));
            }
            return starts;
        }

        /// <summary>Lengths of all completed greens, as (phase, length).</summary>
        public List<(int phase, int length)> GreenLengths()
        {
            var result = new List<(int, int)>();
            int run = 0;
            for (int i = 0; i < Trace.Count; i++)
            {
                bool green = Trace[i].Stage == Stage.Green;
                bool continues = green && i > 0 && Trace[i - 1].Stage == Stage.Green && Trace[i - 1].Phase == Trace[i].Phase;
                if (green && continues)
                {
                    run++;
                    continue;
                }
                if (i > 0 && Trace[i - 1].Stage == Stage.Green && run > 0)
                    result.Add((Trace[i - 1].Phase, run));
                run = green ? 1 : 0;
            }
            return result;
        }

        public static PhaseData Phase(float minGreenSeconds, float maxGreenSeconds, float greenSeconds, PhaseFlags flags = PhaseFlags.None)
        {
            return new PhaseData
            {
                MinGreen = (ushort)SimTime.ToSteps(minGreenSeconds),
                MaxGreen = (ushort)SimTime.ToSteps(maxGreenSeconds),
                Green = (ushort)SimTime.ToSteps(greenSeconds),
                Flags = flags,
            };
        }
    }
}
