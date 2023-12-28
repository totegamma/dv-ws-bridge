using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;

using HarmonyLib;
using FrooxEngine;
using ResoniteModLoader;
using Elements.Core;
using WebSocketSharp;
using WebSocketSharp.Server;

namespace DV_WS_Bridge
{

    internal class WSHandler: WebSocketBehavior
    {
        public delegate void ExternalOnMessage(string message);
        public event ExternalOnMessage externalOnMessage = delegate { };

        public delegate void CustomLog(string message);
        public event CustomLog log = delegate { };

        protected override void OnMessage(MessageEventArgs e)
        {
            base.OnMessage(e);
            externalOnMessage(e.Data);
        }

        public void send(string message)
        {
            Send(message);
        }

        protected override void OnOpen()
        {
            base.OnOpen();
            log("opened!");
        }

        protected override void OnError(ErrorEventArgs e)
        {
            base.OnError(e);
            log("errored! reason:" + e.Message + " exception: " + e.Exception);
        }

        protected override void OnClose(CloseEventArgs e)
        {
            base.OnClose(e);
            log("closed! reason: " + e.Reason + " code: " + e.Code + " wasClean: " + e.WasClean);
        }
    }

    internal class Main : ResoniteMod
    {
        public override string Name => "DV-WS-Interface";
        public override string Author => "totegamma";
        public override string Version => "v0.1.0";

        public delegate void SendBack(string message);

        public struct DVRequest
        {
            public WSHandler handler;
            public string action; // set or get
            public string slotName;
            public string variableName;
            public string type;
            public string value; // only available on set

            static Regex requestSyntax = new Regex(@"(?<action>\w+)\s(?<slotName>\w+)\/(?<variableName>\w+)<(?<type>\w+)>(\s?(?<value>.+))?", RegexOptions.Compiled);

            public DVRequest(WSHandler handler, string str)
            {
                this.handler = handler;
                MatchCollection matches = requestSyntax.Matches(str);
                if (matches.Count == 0)
                {
                    this.action = "";
                    this.slotName = "";
                    this.variableName = "";
                    this.type = "";
                    this.value = "";
                    return;
                }
                GroupCollection groups = matches[0].Groups;
                this.action = groups["action"].Value;
                this.slotName = groups["slotName"].Value;
                this.variableName = groups["variableName"].Value;
                this.type = groups["type"].Value;
                this.value = groups["value"].Value;
            }

            override public string ToString()
            {
                return $"{action} {slotName}/{variableName}<{type}> {value}";
            }
        }

        internal class CacheEntry<T>
        {
            public T? value;
            public DateTime validUntil;
            public CacheEntry(T? value)
            {
                this.value = value;
                this.validUntil = DateTime.Now + TimeSpan.FromSeconds(5) + TimeSpan.FromSeconds(new Random().NextDouble() * 5);
            }

            public void UpdateValidity()
            {
                this.validUntil = DateTime.Now + TimeSpan.FromSeconds(5) + TimeSpan.FromSeconds(new Random().NextDouble() * 5);
            }
        }

        static ConcurrentDictionary<string, CacheEntry<DynamicVariableSpace>> slot_cache = new();
        static ConcurrentQueue<DVRequest> requestQueue = new();

        public override void OnEngineInit()
        {
            Harmony harmony = new Harmony("net.gammalab.resonite.dv_ws_interface");
            harmony.PatchAll();

            WebSocketServer ws = new WebSocketServer(8787);
            ws.AddWebSocketService<WSHandler>("/", x =>
            {
                x.log += (string message) => Msg(message);
                x.externalOnMessage += (string message) =>
                {
                    string[] lines = message.Split('\n');
                    Array.ForEach(lines, (string y) => requestQueue.Enqueue(new DVRequest(x, y)));
                };
            });
            ws.KeepClean = false;
            ws.Start();
        }

        [HarmonyPatch(typeof(FrooxEngine.Engine), "RunUpdateLoop")]
        class Patch
        {
            static bool Prefix(FrooxEngine.Engine __instance)
            {

                if (__instance.WorldManager.FocusedWorld == null) return true;
                var hasItems = requestQueue.TryPeek(out _);
                if (hasItems)
                {
                    __instance.WorldManager.FocusedWorld.RunSynchronously(() =>
                    {
                        Dictionary<WSHandler, List<string>> responces = new Dictionary<WSHandler, List<string>>();
                        while (Main.requestQueue.TryDequeue(out DVRequest job))
                        {
                            if (job.action == "") continue;
                            if (!responces.ContainsKey(job.handler)) responces[job.handler] = new List<string>();

                            var do_slot_lookup = !slot_cache.ContainsKey(job.slotName);

                            // valid check
                            if (!do_slot_lookup) {
                                if (slot_cache[job.slotName].validUntil < DateTime.Now) {
                                    var target = slot_cache[job.slotName].value;
                                    if (target == null) do_slot_lookup = true;
                                    else {
                                        if (target.Parent.Name != job.slotName) {
                                            do_slot_lookup = true;
                                        } else {
                                            slot_cache[job.slotName].UpdateValidity();
                                        }
                                    }
                                }
                            }

                            if (do_slot_lookup)
                            {
                                if (slot_cache.ContainsKey(job.slotName)) {
                                    if (slot_cache[job.slotName].validUntil > DateTime.Now) continue; // don't spam updates
                                    slot_cache.TryRemove(job.slotName, out _);
                                }

                                Slot root = __instance.WorldManager.FocusedWorld.RootSlot;
                                Slot foundSlot = root.FindChild(job.slotName, false, false);
                                if (foundSlot == null) {
                                    responces[job.handler].Add($"ERROR {job} (slot not found)");
                                    slot_cache[job.slotName] = new CacheEntry<DynamicVariableSpace>(null);
                                    continue;
                                }
                                foundSlot.OnPrepareDestroy += (Slot slot) => {
                                    Msg($"destroyed slot {job.slotName}");
                                    slot_cache.TryRemove(job.slotName, out _);
                                };

                                DynamicVariableSpace foundSpace = foundSlot.FindSpace("");
                                if (foundSpace == null) {
                                    responces[job.handler].Add($"ERROR {job} (space not found)");
                                    slot_cache[job.slotName] = new CacheEntry<DynamicVariableSpace>(null);
                                    continue;
                                }
                                foundSpace.Destroyed += (_ic) => {
                                    Msg($"destroyed space {job.slotName}");
                                    slot_cache.TryRemove(job.slotName, out _);
                                };

                                slot_cache[job.slotName] = new CacheEntry<DynamicVariableSpace>(foundSpace);
                            }

                            var space = slot_cache[job.slotName].value;
                            if (space == null)
                            {
                                //responces[job.handler].Add($"ERROR {job} (space not found) (cached)");
                                continue;
                            }

                            bool success = true;

                            if (job.action == "set")
                                switch (job.type)
                                {
                                    case "string":
                                        {
                                            success = space.TryWriteValue<string>(job.variableName, job.value);
                                            break;
                                        }
                                    case "bool":
                                        {
                                            bool ok = bool.TryParse(job.value, out bool parsed);
                                            if (!ok)
                                            {
                                                responces[job.handler].Add($"ERROR {job}");
                                                continue;
                                            }
                                            success = space.TryWriteValue<bool>(job.variableName, parsed);
                                            break;
                                        }
                                    case "int":
                                        {
                                            bool ok = int.TryParse(job.value, out int parsed);
                                            if (!ok)
                                            {
                                                responces[job.handler].Add($"ERROR {job}");
                                                continue;
                                            }
                                            success = space.TryWriteValue<int>(job.variableName, parsed);
                                            break;
                                        }
                                    case "float":
                                        {
                                            bool ok = float.TryParse(job.value, out float parsed);
                                            if (!ok)
                                            {
                                                responces[job.handler].Add($"ERROR {job}");
                                                continue;
                                            }
                                            success = space.TryWriteValue<float>(job.variableName, parsed);
                                            break;
                                        }
                                    case "float2":
                                        {
                                            bool ok = float2.TryParse(job.value, out float2 parsed);
                                            if (!ok)
                                            {
                                                responces[job.handler].Add($"ERROR {job}");
                                                continue;
                                            }
                                            success = space.TryWriteValue<float2>(job.variableName, parsed);
                                            break;
                                        }
                                    case "float3":
                                        {
                                            bool ok = float3.TryParse(job.value, out float3 parsed);
                                            if (!ok)
                                            {
                                                responces[job.handler].Add($"ERROR {job}");
                                                continue;
                                            }
                                            success = space.TryWriteValue<float3>(job.variableName, parsed);
                                            if (!success) responces[job.handler].Add($"ERROR {job}");
                                            break;
                                        }
                                    case "floatQ":
                                        {
                                            bool ok = floatQ.TryParse(job.value, out floatQ parsed);
                                            if (!ok)
                                            {
                                                responces[job.handler].Add($"ERROR {job}");
                                                continue;
                                            }
                                            success = space.TryWriteValue<floatQ>(job.variableName, parsed);
                                            break;
                                        }
                                    default:
                                        responces[job.handler].Add($"ERROR {job} (unknown type)");
                                        break;
                                }
                            if (job.action == "get")
                                switch (job.type)
                                {
                                    case "string":
                                        {
                                            success = space.TryReadValue<string>(job.variableName, out string value);
                                            if (success) responces[job.handler].Add($"VALUE {job.slotName}/{job.variableName}<{job.type}> {value}");
                                            break;
                                        }
                                    case "bool":
                                        {
                                            success = space.TryReadValue<bool>(job.variableName, out bool value);
                                            if (success) responces[job.handler].Add($"VALUE {job.slotName}/{job.variableName}<{job.type}> {value}");
                                            break;
                                        }
                                    case "int":
                                        {
                                            success = space.TryReadValue<int>(job.variableName, out int value);
                                            if (success) responces[job.handler].Add($"VALUE {job.slotName}/{job.variableName}<{job.type}> {value}");
                                            break;
                                        }
                                    case "float":
                                        {
                                            success = space.TryReadValue<float>(job.variableName, out float value);
                                            if (success) responces[job.handler].Add($"VALUE {job.slotName}/{job.variableName}<{job.type}> {value}");
                                            break;
                                        }
                                    case "float2":
                                        {
                                            success = space.TryReadValue<float2>(job.variableName, out float2 value);
                                            if (success) responces[job.handler].Add($"VALUE {job.slotName}/{job.variableName}<{job.type}> {value}");
                                            break;
                                        }
                                    case "float3":
                                        {
                                            success = space.TryReadValue<float3>(job.variableName, out float3 value);
                                            if (success) responces[job.handler].Add($"VALUE {job.slotName}/{job.variableName}<{job.type}> {value}");
                                            break;
                                        }
                                    case "floatQ":
                                        {
                                            success = space.TryReadValue<floatQ>(job.variableName, out floatQ value);
                                            if (success) responces[job.handler].Add($"VALUE {job.slotName}/{job.variableName}<{job.type}> {value}");
                                            break;
                                        }
                                    default:
                                        responces[job.handler].Add($"ERROR {job} (unknown type)");
                                        break;
                                }
                            if (!success) {
                                responces[job.handler].Add($"ERROR {job}");
                                slot_cache[job.slotName] = new CacheEntry<DynamicVariableSpace>(null);
                            }
                        }
                        foreach (KeyValuePair<WSHandler, List<string>> responce in responces)
                        {
                            if (responce.Value.Count == 0) continue;
                            string responceString = string.Join("\n", responce.Value);
                            responce.Key.send(responceString);
                        }
                    });
                }
                return true;
            }
        }
    }
}
