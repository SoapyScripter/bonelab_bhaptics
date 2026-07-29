using BoneLib;
using BoneLib.BoneMenu;
using HarmonyLib;
using Il2CppSLZ.Bonelab;
using Il2CppSLZ.Marrow;
using Il2CppSLZ.Marrow.Data;
using Il2CppSLZ.VRMK;
using MelonLoader;
using MyBhapticsTactsuit;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using static Il2CppSLZ.Marrow.Player_Health;
using static MelonLoader.MelonLogger;


[assembly: MelonInfo(typeof(Bonelab_bhaptics.Bonelab_bhaptics), "Bonelab_bhaptics", "3.3.0", "Florian Fahrenberger/SoapyScripter")]
[assembly: MelonGame("Stress Level Zero", "BONELAB")]

namespace Bonelab_bhaptics
{
    public class Bonelab_bhaptics : MelonMod
    {
        public static TactsuitVR tactsuitVr = null!;
        public static bool playerRightHanded = true;


        public override void OnInitializeMelon()
        {
            tactsuitVr = new TactsuitVR();

            Hooking.OnPostFireGun += OnPostFireGun;
            Hooking.OnPlayerDeath += OnPlayerDeath;
            Hooking.OnSwitchAvatarPostfix += OnSwitchAvatarPostfix;

            BoneLib.BoneMenu.Page myPage = BoneLib.BoneMenu.Page.Root.CreatePage("bHaptics Mod", Color.yellow);

            myPage.CreateBool("Suit Connected", Color.white, !tactsuitVr.suitDisabled, (value) => tactsuitVr.suitDisabled = value);
            myPage.CreateBool("Visor Connected", Color.white, tactsuitVr.faceConnected, (value) => tactsuitVr.faceConnected = value);
            myPage.CreateBool("Sleeves Connected", Color.white, tactsuitVr.armsConnected, (value) => tactsuitVr.armsConnected = value);


            myPage.CreateFunction("Create New Tactsuit", Color.yellow, () => tactsuitVr = new TactsuitVR());
            myPage.CreateFunction("Test Haptics", Color.yellow, () => tactsuitVr.PlaybackHaptics("SwitchAvatar"));


        }

        private void OnPostFireGun(Gun gun)
        {
            bool rightHanded = false;
            bool twoHanded = false;

            if (gun == null) return;
            if (gun.triggerGrip == null) return;
            if (gun.chamberedCartridge == null) return;
            twoHanded = (gun.triggerGrip.attachedHands.Count > 1);

            foreach (var hand in gun.triggerGrip.attachedHands)
            {
                if (Player.ControllerRig != hand.Controller.contRig) { return; }
                if (hand.handedness == Il2CppSLZ.Marrow.Interaction.Handedness.RIGHT) { rightHanded = true; break; }
            }
            
            if (!twoHanded)
            {
                foreach (var grip in gun.otherGrips)
                {
                    if (!twoHanded)
                    {
                        twoHanded = (grip.attachedHands.Count > 0);
                        break;
                    }
                }
            }

            float intensity = Mathf.Min(gun.muzzleVelocity / 1000.0f, 1f);
            tactsuitVr.GunRecoil(rightHanded, intensity, twoHanded);
        }

        private static KeyValuePair<float, float> getAngleAndShift(Transform player, Vector3 hit)
        {
            // bhaptics pattern starts in the front, then rotates to the left. 0° is front, 90° is left, 270° is right.
            // y is "up", z is "forward" in local coordinates
            Vector3 patternOrigin = new Vector3(0f, 0f, 1f);
            Vector3 hitPosition = hit - player.position;
            Quaternion myPlayerRotation = player.rotation;
            Vector3 playerDir = myPlayerRotation.eulerAngles;
            // get rid of the up/down component to analyze xz-rotation
            Vector3 flattenedHit = new Vector3(hitPosition.x, 0f, hitPosition.z);

            // get angle. .Net < 4.0 does not have a "SignedAngle" function...
            float hitAngle = Vector3.Angle(flattenedHit, patternOrigin);
            // check if cross product points up or down, to make signed angle myself
            Vector3 crossProduct = Vector3.Cross(flattenedHit, patternOrigin);
            if (crossProduct.y < 0f) { hitAngle *= -1f; }
            // relative to player direction
            float myRotation = hitAngle - playerDir.y;
            // switch directions (bhaptics angles are in mathematically negative direction)
            myRotation *= -1f;
            // convert signed angle into [0, 360] rotation
            if (myRotation < 0f) { myRotation = 360f + myRotation; }


            // up/down shift is in y-direction
            // in Battle Sister, the torso Transform has y=0 at the neck,
            // and the torso ends at roughly -0.5 (that's in meters)
            // so cap the shift to [-0.5, 0]...
            float hitShift = hitPosition.y;
            //tactsuitVr.LOG("HitShift: " + hitShift.ToString());
            float upperBound = 0.5f;
            float lowerBound = -0.5f;
            if (hitShift > upperBound) { hitShift = 0.5f; }
            else if (hitShift < lowerBound) { hitShift = -0.5f; }
            // ...and then spread/shift it to [-0.5, 0.5], which is how bhaptics expects it
            else { hitShift = (hitShift - lowerBound) / (upperBound - lowerBound) - 0.5f; }

            // No tuple returns available in .NET < 4.0, so this is the easiest quickfix
            return new KeyValuePair<float, float>(myRotation, hitShift);
        }

        [HarmonyPatch(typeof(PlayerDamageReceiver), "ReceiveAttack", new Type[] { typeof(Il2CppSLZ.Marrow.Combat.Attack) })]
        public class bhaptics_ReceiveAttack
        {
            [HarmonyPostfix]
            public static void Postfix(PlayerDamageReceiver __instance, Il2CppSLZ.Marrow.Combat.Attack attack)
            {
                if (Player.RigManager != null)
                {
                    if (__instance.health._rigManager != Player.RigManager) return;
                    string damagePattern;
                    bool hapticsApplied = false;
                    switch (attack.attackType)
                    {
                        case Il2CppSLZ.Marrow.Data.AttackType.Piercing:
                            damagePattern = "BulletHit";
                            break;
                        case Il2CppSLZ.Marrow.Data.AttackType.Blunt:
                            damagePattern = "Impact";
                            break;
                        case Il2CppSLZ.Marrow.Data.AttackType.Fire:
                            damagePattern = "LavaballHit";
                            break;
                        case Il2CppSLZ.Marrow.Data.AttackType.Slicing:
                            damagePattern = "BladeHit";
                            break;
                        case Il2CppSLZ.Marrow.Data.AttackType.Stabbing:
                            damagePattern = "BulletHit";
                            break;
                        default:
                            damagePattern = "Impact";
                            break;
                    }
                    if (__instance.bodyPart == PlayerDamageReceiver.BodyPart.Head)
                    {
                        if (tactsuitVr.faceConnected)
                        {
                            tactsuitVr.PlaybackHaptics("Headshot_F");
                            hapticsApplied = true;
                        }
                    }
                    if ((__instance.bodyPart == PlayerDamageReceiver.BodyPart.ArmLowerLf) || (__instance.bodyPart == PlayerDamageReceiver.BodyPart.ArmUpperLf))
                    {
                        if (tactsuitVr.armsConnected)
                        {
                            tactsuitVr.PlaybackHaptics("Recoil_L");
                            hapticsApplied = true;
                        }
                    }
                    if ((__instance.bodyPart == PlayerDamageReceiver.BodyPart.ArmLowerRt) || (__instance.bodyPart == PlayerDamageReceiver.BodyPart.ArmUpperRt))
                    {
                        if (tactsuitVr.armsConnected)
                        {
                            tactsuitVr.PlaybackHaptics("Recoil_R");
                            hapticsApplied = true;
                        }
                    }
                    if ((!hapticsApplied))
                    {
                        KeyValuePair<float, float> angleShift;
                        if (attack.collider != null) { angleShift = getAngleAndShift(__instance.transform, attack.collider.transform.position); }
                        else { angleShift = getAngleAndShift(__instance.transform, attack.origin); }
                        tactsuitVr.PlayBackHit(damagePattern, angleShift.Key, angleShift.Value);
                    }
                }
            }
        }


        [HarmonyPatch(typeof(InventorySlotReceiver), "OnHandGrab", new Type[] { typeof(Hand) })]
        public class bhaptics_SlotGrab
        {
            [HarmonyPostfix]
            public static void Postfix(InventorySlotReceiver __instance, Hand hand)
            {
                if (__instance.isInUIMode) return;
                if (hand == null) return;
                if (Player.RigManager != null)
                {
                    if (hand.manager != Player.RigManager) return;
                    string name = "BackCt";

                    foreach (var slot in Player.RigManager.inventory.bodySlots)
                    {
                        if (slot.inventorySlotReceiver == __instance)
                        {
                            name = slot.name;
                            break;
                        }
                    }

                    if (name == "SideLf") { tactsuitVr.PlaybackHaptics("StoreGun_L"); }
                    else if (name == "SideRt") { tactsuitVr.PlaybackHaptics("StoreGun_R"); }
                    else if (name == "BackLf") { tactsuitVr.PlaybackHaptics("ReceiveShoulder_L"); }
                    else if (name == "BackRt") { tactsuitVr.PlaybackHaptics("ReceiveShoulder_R"); }
                    else { tactsuitVr.PlaybackHaptics("StoreGun_R"); }
                }
            }
        }

        [HarmonyPatch(typeof(InventorySlotReceiver), "OnHandDrop", new Type[] { typeof(IGrippable) })]
        public class bhaptics_SlotInsert
        {
            [HarmonyPostfix]
            public static void Postfix(InventorySlotReceiver __instance, IGrippable host)
            {
                Hand hand = host.GetLastHand();
                if (__instance.isInUIMode) return;
                if (hand == null) return;
                if (Player.RigManager != null)
                {
                    if (hand.manager != Player.RigManager) return;
                    string name = "BackCt";

                    foreach (var slot in Player.RigManager.inventory.bodySlots)
                    {
                        if (slot.inventorySlotReceiver == __instance)
                        {
                            name = slot.name;
                            break;
                        }
                    }

                    if (name == "SideLf") { tactsuitVr.PlaybackHaptics("StoreGun_L"); }
                    else if (name == "SideRt") { tactsuitVr.PlaybackHaptics("StoreGun_R"); }
                    else if (name == "BackLf") { tactsuitVr.PlaybackHaptics("StoreShoulder_L"); }
                    else if (name == "BackRt") { tactsuitVr.PlaybackHaptics("StoreShoulder_R"); }
                    else { tactsuitVr.PlaybackHaptics("StoreGun_R"); }
                }
            }
        }

        private void OnPlayerDeath(RigManager rm)
        {
            if (Player.RigManager != null)
            {
                if (rm != Player.RigManager) return;
                tactsuitVr.StopThreads();
            }
        }

        [HarmonyPatch(typeof(Player_Health), "UpdateHealth", new Type[] { typeof(float) })]
        public class bhaptics_PlayerHealthUpdate
        {
            [HarmonyPostfix]
            public static void Postfix(Player_Health __instance)
            {
                if (Player.RigManager != null)
                {
                    if (__instance._rigManager != Player.RigManager) return;
                    if (__instance.curr_Health <= 0.3f * __instance.max_Health) tactsuitVr.StartHeartBeat();
                    else tactsuitVr.StopHeartBeat();
                }
            }
        }

        private void OnSwitchAvatarPostfix(Avatar avatar)
        {
            if (Player.RigManager != null)
            {
                if (avatar != Player.Avatar) return;
                tactsuitVr.PlaybackHaptics("SwitchAvatar");
            }
        }
    }
}
