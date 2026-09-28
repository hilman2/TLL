using System.Collections.Generic;
using Game;
using Game.Common;
using Game.Net;
using Game.Objects;
using Game.Pathfind;
using Game.Simulation;
using Game.Tools;
using Game.Vehicles;
using TLL.Components;
using TLL.Core;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace TLL.Systems
{
    /// <summary>
    /// A green wave for emergency vehicles along their whole route. An
    /// ambulance, fire engine or police car on an emergency call asks for
    /// green at every junction TLL runs that it reaches within
    /// <see cref="kLeadSeconds"/>, for the movement its route takes there,
    /// and keeps it until it has passed. The controllers serve such a
    /// request as they serve an emergency vehicle at the line: the running
    /// green ends after its minimum, and the green of the vehicle's movement
    /// holds while it is on its way (SignalController, Preempt).
    ///
    /// The game's own request comes only from a vehicle at the line, too
    /// late for the junction to clear before it; so does the game's own
    /// traffic light control. The route is known: the lane the vehicle is
    /// on, the lanes it has planned in detail (CarNavigationLane), and the
    /// path beyond (PathElement, from PathOwner.m_ElementIndex).
    /// </summary>
    public partial class EmergencyRouteSystem : TllSystemBase
    {
        /// <summary>Seconds before an emergency vehicle reaches a junction that its movement there gets green.</summary>
        public const float kLeadSeconds = 30f;

        /// <summary>
        /// Speed assumed for a vehicle slower than this, in metres per
        /// second: one standing at a red light, or just starting, still
        /// clears the junctions ahead of it.
        /// </summary>
        private const float kMinSpeed = 12f;

        /// <summary>Limits of the route looked at: metres ahead, and path elements.</summary>
        private const float kMaxDistance = 1500f;
        private const int kMaxElements = 96;

        /// <summary>Frames between two looks at the routes: about half a second.</summary>
        private const int kInterval = 32;

        /// <summary>
        /// Seconds a junction holds green for vehicles on their way before it
        /// lets the request go: a vehicle stuck for that long is not coming
        /// soon, and the other directions would wait for nothing.
        /// </summary>
        private const float kMaxHoldSeconds = 90f;

        private SimulationSystem m_Simulation;
        private EntityQuery m_Query;

        /// <summary>Per junction asked for without a break, the controller step it was first asked at.</summary>
        private readonly Dictionary<Entity, long> m_AskedSince = new Dictionary<Entity, long>();

        public override int GetUpdateInterval(SystemUpdatePhase phase)
        {
            return kInterval;
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Simulation = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_Query = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Car>(), ComponentType.ReadOnly<CarCurrentLane>(), ComponentType.ReadOnly<PathOwner>() },
                Any = new[] { ComponentType.ReadOnly<Ambulance>(), ComponentType.ReadOnly<FireEngine>(), ComponentType.ReadOnly<PoliceCar>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() },
            });
            RequireForUpdate(m_Query);
        }

        protected override void OnSafeUpdate()
        {
            Setting settings = Mod.Settings;
            if (settings == null || !settings.EmergencyGreenWave)
                return;
            var requests = new Dictionary<Entity, ulong>();
            using (NativeArray<Entity> vehicles = m_Query.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity vehicle in vehicles)
                {
                    if ((EntityManager.GetComponentData<Car>(vehicle).m_Flags & CarFlags.Emergency) == 0)
                        continue;
                    float speed = EntityManager.HasComponent<Moving>(vehicle) ? math.length(EntityManager.GetComponentData<Moving>(vehicle).m_Velocity) : 0f;
                    Follow(vehicle, math.min(kMaxDistance, math.max(speed, kMinSpeed) * kLeadSeconds), requests);
                }
            }
            var gone = new List<Entity>();
            foreach (Entity node in m_AskedSince.Keys)
            {
                if (!requests.ContainsKey(node))
                    gone.Add(node);
            }
            foreach (Entity node in gone)
                m_AskedSince.Remove(node);
            if (requests.Count == 0)
                return;
            // A request holds a little longer than until the next look, so it
            // does not lapse in between; once the vehicle has passed, the next
            // look no longer renews it.
            long now = SimTime.StepOfFrame(m_Simulation.frameIndex);
            long until = now + SimTime.ToSteps(2f * kInterval / 60f + 1f);
            foreach (KeyValuePair<Entity, ulong> request in requests)
            {
                if (!m_AskedSince.TryGetValue(request.Key, out long since))
                    m_AskedSince[request.Key] = since = now;
                if (now - since > SimTime.ToSteps(kMaxHoldSeconds))
                    continue;
                JunctionRuntime runtime = EntityManager.GetComponentData<JunctionRuntime>(request.Key);
                runtime.EmergencyMovements = request.Value;
                runtime.EmergencyUntil = until;
                EntityManager.SetComponentData(request.Key, runtime);
            }
        }

        /// <summary>Walks the vehicle's route up to <paramref name="horizon"/> metres and adds the movements it takes at junctions TLL runs.</summary>
        private void Follow(Entity vehicle, float horizon, Dictionary<Entity, ulong> requests)
        {
            CarCurrentLane current = EntityManager.GetComponentData<CarCurrentLane>(vehicle);
            // The lane it is on counts too: a vehicle inside the junction
            // keeps its green until it is out.
            Request(current.m_Lane, requests);
            float distance = Length(current.m_Lane) * math.abs(current.m_CurvePosition.z - current.m_CurvePosition.x);
            if (EntityManager.HasBuffer<CarNavigationLane>(vehicle))
            {
                DynamicBuffer<CarNavigationLane> planned = EntityManager.GetBuffer<CarNavigationLane>(vehicle, true);
                for (int i = 0; i < planned.Length && distance <= horizon; i++)
                {
                    Request(planned[i].m_Lane, requests);
                    distance += Length(planned[i].m_Lane) * math.abs(planned[i].m_CurvePosition.y - planned[i].m_CurvePosition.x);
                }
            }
            if (!EntityManager.HasBuffer<PathElement>(vehicle))
                return;
            DynamicBuffer<PathElement> path = EntityManager.GetBuffer<PathElement>(vehicle, true);
            int start = math.max(0, EntityManager.GetComponentData<PathOwner>(vehicle).m_ElementIndex);
            for (int i = start; i < path.Length && i < start + kMaxElements && distance <= horizon; i++)
            {
                Request(path[i].m_Target, requests);
                distance += Length(path[i].m_Target) * math.abs(path[i].m_TargetDelta.y - path[i].m_TargetDelta.x);
            }
        }

        /// <summary>Adds the movement a lane belongs to, if it is a lane of a junction TLL runs.</summary>
        private void Request(Entity lane, Dictionary<Entity, ulong> requests)
        {
            if (lane == Entity.Null || !EntityManager.HasComponent<Owner>(lane))
                return;
            Entity node = EntityManager.GetComponentData<Owner>(lane).m_Owner;
            if (!EntityManager.HasComponent<ManagedJunction>(node) || !EntityManager.HasBuffer<JunctionLane>(node)
                || !EntityManager.HasComponent<JunctionRuntime>(node))
                return;
            DynamicBuffer<JunctionLane> lanes = EntityManager.GetBuffer<JunctionLane>(node, true);
            for (int i = 0; i < lanes.Length; i++)
            {
                if (lanes[i].Lane != lane || lanes[i].Movement >= 64)
                    continue;
                requests.TryGetValue(node, out ulong movements);
                requests[node] = movements | (1UL << lanes[i].Movement);
                return;
            }
        }

        private float Length(Entity lane)
        {
            return lane != Entity.Null && EntityManager.HasComponent<Curve>(lane) ? EntityManager.GetComponentData<Curve>(lane).m_Length : 0f;
        }
    }
}
