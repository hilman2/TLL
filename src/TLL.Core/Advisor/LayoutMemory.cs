using System;

namespace TLL.Core.Advisor
{
    /// <summary>How one layout has done at a junction, measured against <see cref="DelayModel"/>.</summary>
    public struct Calibration
    {
        /// <summary>
        /// Measured vehicle delay over the delay DelayModel expected for the
        /// same traffic, smoothed over the measurement periods. Above 1 the
        /// layout does worse in the game than on paper.
        /// </summary>
        public float Factor;

        /// <summary>Measurement periods behind <see cref="Factor"/>; 0 means never measured.</summary>
        public ushort Samples;

        /// <summary>
        /// How often a measurement period of this layout ended with a queue
        /// that does not clear, smoothed, from 0 to 1.
        /// </summary>
        public float Backlog;

        public bool Measured => Samples > 0;
    }

    /// <summary>
    /// What a junction has learnt about its layouts by running them: for
    /// each, how far the game's waiting times are from the model's, running
    /// alone and in a green wave, and whether queues built up. The autopilot
    /// corrects the model's estimates with it (JunctionAdvisor.Correct), so
    /// a layout that looks good on paper but does badly in the game is not
    /// chosen again, and one the model rates too harshly gets its chance.
    /// </summary>
    /// <remarks>
    /// The measurement is kept as a ratio to the model, not as seconds,
    /// because the traffic changes between periods: the ratio compares a
    /// rush hour with the quiet afternoon, the seconds do not.
    /// </remarks>
    public struct LayoutMemory
    {
        /// <summary>Share of a new period in <see cref="Calibration.Factor"/>.</summary>
        public const float Weight = 0.3f;

        /// <summary>
        /// Share of a new period in <see cref="Calibration.Backlog"/>. Higher
        /// than for the factor: one period with a queue that does not clear
        /// is enough for the layout to count as not coping.
        /// </summary>
        public const float BacklogWeight = 0.5f;

        /// <summary>A measured layout with at least this much backlog counts as not coping.</summary>
        public const float BacklogShare = 0.5f;

        /// <summary>
        /// Per review, the backlog remembered for a layout that is not
        /// running fades by this factor: from certain to below
        /// <see cref="BacklogShare"/> in five reviews, 7.5 game hours. A
        /// layout that failed in the rush hour gets another chance later.
        /// </summary>
        public const float BacklogFade = 0.85f;

        /// <summary>Limits of one measurement against the model; beyond them it says more about the measurement than the layout.</summary>
        public const float MinFactor = 0.2f;
        public const float MaxFactor = 5f;

        /// <summary>Model delay below which a period is not compared, in seconds per vehicle: the ratio would be noise.</summary>
        public const float MinModelled = 0.5f;

        // Per JunctionAdvisor.Strategies index; a struct of fields rather
        // than an array, so it stays plain data for the ECS. Slot 3 held
        // the pedestrian scramble layout, which is now a layout with a
        // scramble; saves carry it over to Scramble0 (AutopilotState).
        public Calibration Alone0, Alone1, Alone2, Alone3;
        public Calibration Wave0, Wave1, Wave2, Wave3;

        /// <summary>
        /// Per layout, run with a scramble: a plan with a phase of its own
        /// for pedestrians and no turn waiting for people, which the plain
        /// layout's measurement says nothing about. In a wave or not alike:
        /// such a junction rarely runs in a wave.
        /// </summary>
        public Calibration Scramble0, Scramble1, Scramble2, Scramble3;

        /// <summary>Slots per kind of measurement; more than there are layouts, for the saves.</summary>
        public const int Layouts = 4;

        public Calibration Get(int layout, bool wave, bool scramble)
        {
            if (!scramble)
                return Get(layout, wave);
            switch (layout)
            {
                case 0: return Scramble0;
                case 1: return Scramble1;
                case 2: return Scramble2;
                case 3: return Scramble3;
                default: return default;
            }
        }

        public void Set(int layout, bool wave, bool scramble, Calibration value)
        {
            if (!scramble)
            {
                Set(layout, wave, value);
                return;
            }
            switch (layout)
            {
                case 0: Scramble0 = value; break;
                case 1: Scramble1 = value; break;
                case 2: Scramble2 = value; break;
                case 3: Scramble3 = value; break;
            }
        }

        public Calibration Get(int layout, bool wave)
        {
            switch (wave ? layout + Layouts : layout)
            {
                case 0: return Alone0;
                case 1: return Alone1;
                case 2: return Alone2;
                case 3: return Alone3;
                case 4: return Wave0;
                case 5: return Wave1;
                case 6: return Wave2;
                case 7: return Wave3;
                default: return default;
            }
        }

        public void Set(int layout, bool wave, Calibration value)
        {
            switch (wave ? layout + Layouts : layout)
            {
                case 0: Alone0 = value; break;
                case 1: Alone1 = value; break;
                case 2: Alone2 = value; break;
                case 3: Alone3 = value; break;
                case 4: Wave0 = value; break;
                case 5: Wave1 = value; break;
                case 6: Wave2 = value; break;
                case 7: Wave3 = value; break;
            }
        }

        /// <summary>Records one measurement period of the running layout.</summary>
        /// <param name="measured">Mean vehicle delay measured over the period, in seconds.</param>
        /// <param name="modelled">Mean vehicle delay DelayModel expects for the period's traffic, in seconds.</param>
        /// <param name="backlog">A queue built up that did not clear.</param>
        public void Record(int layout, bool wave, float measured, float modelled, bool backlog)
        {
            Record(layout, wave, false, measured, modelled, backlog);
        }

        /// <summary>Records one measurement period of the running layout, with a scramble or without.</summary>
        public void Record(int layout, bool wave, bool scramble, float measured, float modelled, bool backlog)
        {
            if (layout < 0 || layout >= Layouts || modelled < MinModelled)
                return;
            Calibration c = Get(layout, wave, scramble);
            float ratio = Math.Min(MaxFactor, Math.Max(MinFactor, measured / modelled));
            float jammed = backlog ? 1f : 0f;
            if (c.Measured)
            {
                c.Factor += Weight * (ratio - c.Factor);
                c.Backlog += BacklogWeight * (jammed - c.Backlog);
            }
            else
            {
                c.Factor = ratio;
                c.Backlog = jammed;
            }
            if (c.Samples < ushort.MaxValue)
                c.Samples++;
            Set(layout, wave, scramble, c);
        }

        /// <summary>Fades the backlog remembered for the layouts other than <paramref name="running"/>; called once per review.</summary>
        public void Fade(int running)
        {
            Fade(running, false);
        }

        /// <summary>
        /// Fades the backlog remembered for everything but what runs:
        /// <paramref name="running"/>, with a scramble or without. Called
        /// once per review.
        /// </summary>
        public void Fade(int running, bool scramble)
        {
            for (int i = 0; i < Layouts * 3; i++)
            {
                int layout = i % Layouts;
                bool wave = i >= Layouts && i < Layouts * 2;
                bool slotScramble = i >= Layouts * 2;
                if (layout == running && slotScramble == scramble)
                    continue;
                Calibration c = Get(layout, wave, slotScramble);
                c.Backlog *= BacklogFade;
                Set(layout, wave, slotScramble, c);
            }
        }
    }
}
