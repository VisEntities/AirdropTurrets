using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Airdrop Turrets", "VisEntities", "1.0.0")]
    [Description("Deploys auto turrets onto airdrops.")]
    public class AirdropTurrets : RustPlugin
    {
        #region Fields

        private static AirdropTurrets _plugin;
        private static Configuration _config;

        private List<AutoTurret> _supplyDropTurrets = new List<AutoTurret>();
        private static readonly Vector3 _autoTurretPosition = new Vector3(-0.09f, 2.60f, -0.07f);
        private const string PREFAB_AUTO_TURRET = "assets/prefabs/npc/autoturret/autoturret_deployed.prefab";

        #endregion Fields

        #region Configuration

        private class Configuration
        {
            [JsonProperty("Version")]
            public string Version { get; set; }

            [JsonProperty("Weapon Shortname")]
            public string WeaponShortname { get; set; }

            [JsonProperty("Clip Ammo")]
            public AmmoInfo ClipAmmo { get; set; }

            [JsonProperty("Reserve Ammo")]
            public List<AmmoInfo> ReserveAmmo { get; set; }

            [JsonProperty("Attachment Shortnames")]
            public List<string> AttachmentShortnames { get; set; }

            [JsonProperty("Peacekeeper")]
            public bool Peacekeeper { get; set; }
        }

        private class AmmoInfo
        {
            [JsonProperty("Shortname")]
            public string Shortname { get; set; }

            [JsonProperty("Amount")]
            public int Amount { get; set; }
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            _config = Config.ReadObject<Configuration>();

            if (string.Compare(_config.Version, Version.ToString()) < 0)
                UpdateConfig();

            SaveConfig();
        }

        protected override void LoadDefaultConfig()
        {
            _config = GetDefaultConfig();
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(_config, true);
        }

        private void UpdateConfig()
        {
            PrintWarning("Config changes detected! Updating...");

            Configuration defaultConfig = GetDefaultConfig();

            if (string.Compare(_config.Version, "1.0.0") < 0)
                _config = defaultConfig;

            PrintWarning("Config update complete! Updated from version " + _config.Version + " to " + Version.ToString());
            _config.Version = Version.ToString();
        }

        private Configuration GetDefaultConfig()
        {
            return new Configuration
            {
                Version = Version.ToString(),
                WeaponShortname = "rifle.ak",
                ClipAmmo = new AmmoInfo
                {
                    Shortname = "ammo.rifle",
                    Amount = 30
                },
                ReserveAmmo = new List<AmmoInfo>
                {
                    new AmmoInfo
                    {
                        Shortname = "ammo.rifle",
                        Amount = 128
                    },
                    new AmmoInfo
                    {
                        Shortname = "ammo.rifle",
                        Amount = 128
                    }
                },
                AttachmentShortnames = new List<string>
                {
                    "weapon.mod.lasersight"
                },
                Peacekeeper = true
            };
        }

        #endregion Configuration

        #region Oxide Hooks

        private void Init()
        {
            _plugin = this;
        }

        private void Unload()
        {
            KillAllSupplyDropTurrets();
            _config = null;
            _plugin = null;
        }

        private void OnServerInitialized(bool isStartup)
        {
            foreach (SupplyDrop supplyDrop in BaseNetworkable.serverEntities.OfType<SupplyDrop>())
            {
                if (supplyDrop == null || supplyDrop.isLootable)
                    continue;

                List<AutoTurret> turrets = FindChildrenOfType<AutoTurret>(supplyDrop);
                if (turrets.Count > 0)
                    continue;

                DeployAutoTurret(supplyDrop);
            }
        }

        private void OnEntitySpawned(SupplyDrop supplyDrop)
        {
            if (supplyDrop == null || supplyDrop.isLootable)
                return;

            NextTick(() =>
            {
                DeployAutoTurret(supplyDrop);
                supplyDrop.CancelInvoke(supplyDrop.CheckNightLight);
            });
        }

        #endregion Oxide Hooks

        #region Turret Deployment and Setup

        private void DeployAutoTurret(SupplyDrop supplyDrop)
        {
            AutoTurret autoTurret = SpawnAutoTurret(supplyDrop, _autoTurretPosition, Quaternion.identity);
            if (autoTurret != null)
            {
                AddWeaponToTurret(autoTurret);

                LoadTurretWithReserveAmmo(autoTurret.inventory);
                autoTurret.UpdateTotalAmmo();
                autoTurret.EnsureReloaded();

                autoTurret.SetPeacekeepermode(_config.Peacekeeper);
                autoTurret.InitiateStartup();

                autoTurret.SendNetworkUpdate();
                _supplyDropTurrets.Add(autoTurret);
            }
        }

        private AutoTurret SpawnAutoTurret(SupplyDrop supplyDrop, Vector3 position, Quaternion rotation)
        {
            AutoTurret autoTurret = GameManager.server.CreateEntity(PREFAB_AUTO_TURRET, position, rotation) as AutoTurret;
            if (autoTurret == null)
                return null;

            autoTurret.SetParent(supplyDrop);
            autoTurret.Spawn();

            RemoveProblematicComponents(autoTurret);
            HideIOInputsAndOutputs(autoTurret);

            return autoTurret;
        }

        private Item AddWeaponToTurret(AutoTurret autoTurret)
        {
            Item item = ItemManager.CreateByName(_config.WeaponShortname);
            if (item == null)
                return null;

            if (_config.AttachmentShortnames != null)
            {
                foreach (string attachmentShortname in _config.AttachmentShortnames)
                {
                    var attachmentItem = ItemManager.CreateByName(attachmentShortname);
                    if (attachmentItem != null)
                    {
                        if (!attachmentItem.MoveToContainer(item.contents))
                        {
                            attachmentItem.Remove();
                        }
                    }
                }
            }

            if (!item.MoveToContainer(autoTurret.inventory, 0))
            {
                item.Remove();
                return null;
            }

            BaseProjectile weapon = item.GetHeldEntity() as BaseProjectile;
            if (weapon != null)
            {
                if (_config.AttachmentShortnames != null)
                {
                    // Ensures the weapon's magazine capacity reflects the modifications applied, such as the extended magazine mod.
                    weapon.DelayedModsChanged();
                    weapon.CancelInvoke(weapon.DelayedModsChanged);
                }

                autoTurret.UpdateAttachedWeapon();
                autoTurret.CancelInvoke(autoTurret.UpdateAttachedWeapon);

                if (_config.ClipAmmo != null)
                {
                    ItemDefinition loadedAmmoItemDefinition = ItemManager.FindItemDefinition(_config.ClipAmmo.Shortname);
                    if (loadedAmmoItemDefinition != null)
                    {
                        weapon.primaryMagazine.ammoType = loadedAmmoItemDefinition;
                        weapon.primaryMagazine.contents = Mathf.Min(weapon.primaryMagazine.capacity, _config.ClipAmmo.Amount);
                    }
                }
            }

            return item;
        }

        private void LoadTurretWithReserveAmmo(ItemContainer autoTurretContainer)
        {
            if (_config.ReserveAmmo == null)
                return;

            // Starting from slot 1, because slot 0 is reserved for the weapon.
            int currentSlot = 1;
            int maximumAvailableSlot = autoTurretContainer.capacity - 1;

            foreach (AmmoInfo ammo in _config.ReserveAmmo)
            {
                if (currentSlot > maximumAvailableSlot)
                    break;

                if (ammo.Amount <= 0)
                    continue;

                ItemDefinition itemDefinition = ItemManager.FindItemDefinition(ammo.Shortname);
                if (itemDefinition == null)
                    continue;

                int amountToAdd = Math.Min(ammo.Amount, itemDefinition.stackable);
                Item ammoItem = ItemManager.Create(itemDefinition, amountToAdd);
                if (!ammoItem.MoveToContainer(autoTurretContainer, currentSlot))
                {
                    ammoItem.Remove();
                }

                if (ammoItem.parent != autoTurretContainer)
                {
                    Item destinationItem = autoTurretContainer.GetSlot(currentSlot);
                    if (destinationItem != null)
                    {
                        destinationItem.amount = amountToAdd;
                        destinationItem.MarkDirty();
                    }

                    ammoItem.Remove();
                }

                currentSlot++;
            }
        }

        #endregion Turret Deployment and Setup

        #region Turret Cleanup

        private void KillAllSupplyDropTurrets()
        {
            foreach (AutoTurret turret in _supplyDropTurrets)
            {
                if (turret != null)
                    turret.Kill();
            }

            _supplyDropTurrets.Clear();
        }

        #endregion Turret Cleanup

        #region Helper Functions

        private static void RemoveProblematicComponents(BaseEntity entity)
        {
            foreach (var collider in entity.GetComponentsInChildren<Collider>())
            {
                if (!collider.isTrigger)
                    UnityEngine.Object.DestroyImmediate(collider);
            }

            UnityEngine.Object.DestroyImmediate(entity.GetComponent<DestroyOnGroundMissing>());
            UnityEngine.Object.DestroyImmediate(entity.GetComponent<GroundWatch>());
        }

        private static void HideIOInputsAndOutputs(IOEntity ioEntity)
        {
            foreach (var input in ioEntity.inputs)
                input.type = IOEntity.IOType.Generic;

            foreach (var output in ioEntity.outputs)
                output.type = IOEntity.IOType.Generic;
        }

        private static List<T> FindChildrenOfType<T>(BaseEntity parentEntity, string prefabName = null) where T : BaseEntity
        {
            List<T> foundChildren = new List<T>();
            foreach (BaseEntity child in parentEntity.children)
            {
                T childOfType = child as T;
                if (childOfType != null && (prefabName == null || child.PrefabName == prefabName))
                    foundChildren.Add(childOfType);
            }

            return foundChildren;
        }

        #endregion Helper Functions
    }
}