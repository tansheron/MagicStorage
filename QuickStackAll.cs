using System.Collections.Generic;
using System.Linq;
using MagicStorage.Components;
using MagicStorage.Items;
using Microsoft.Xna.Framework;
using MonoMod.RuntimeDetour.HookGen;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.ModLoader;
using CraftingAccess = MagicStorage.Components.CraftingAccess;
using StorageAccess = MagicStorage.Components.StorageAccess;
using StorageHeart = MagicStorage.Components.StorageHeart;

namespace MagicStorage
{
	internal static class QuickStackAll
	{
		internal static void InsertQuickStackAllChestsPatch(HookIL il)
		{
			// In Terraria 1.3.5.3, the quick-stack sound is the safest way to tell where to patch.
			// It happens once when one or more items were quick-stacked and does not happen more than once.
			// It also appears only once in the unpatched body of Terraria.Player.QuickStackAllChests.
			var cursor = il.At(0);
			var found = cursor.TryGotoNext(
				i => i.MatchCall<Main>("PlaySound")
			);

			if (found)
			{
				cursor.EmitDelegate(RefreshItems);
			}
			else
			{
				ErrorLogger.Log(
					"No suitable patch site for refreshing MagicStorage's storage UI was found in Terraria.Player.QuickStackAllChests. MagicStorage will not support quick-stacking to nearby storage hearts.");
			}
		}

		internal static Item PutItemInNearbyChest(On.Terraria.Chest.orig_PutItemInNearbyChest orig, Item item, Vector2 position)
		{
			return QuickStackItemInternal(orig, item, position, null);
		}

		internal static void ServerPlaceItem(On.Terraria.Chest.orig_ServerPlaceItem orig, int plr, int slot)
		{
			var player = Main.player[plr];
			var inventory = player.inventory;

			// Chest.PutItemInNearbyChest is hooked, so QuickStackItemInternal will be called multiple times if quick-stacking falls through to checking vanilla chests.
			// This doesn't hurt anything but wastes a bit of time.
			// The alternative would be using a mutex to block other threads from accessing the original method while we remove the hook and call the original method.
			// The vanilla code for Player.QuickStackAllChests would have to be patched to take the mutex.
			inventory[slot] = QuickStackItemInternal(Chest.PutItemInNearbyChest, inventory[slot], player.Center, player);
			NetMessage.SendData(MessageID.SyncEquipment, number: plr, number2: slot, number3: inventory[slot].prefix);
		}

		private static Item QuickStackItemInternal(On.Terraria.Chest.orig_PutItemInNearbyChest orig, Item item, Vector2 position, Player player)
		{
			var result = Execute(item, position, player);

			if (Main.netMode == NetmodeID.MultiplayerClient || item.stack > 0)
			{
				result = orig(item, position);
			}

			return result;
		}
		
		private static void RefreshItems()
		{
			if (Main.netMode != NetmodeID.Server)
			{
				StorageGUI.RefreshItems();
			}
		}

		private static Item Execute(Item item, Vector2 playerPosition, Player player)
		{
			if (Main.netMode == NetmodeID.MultiplayerClient)
			{
				return item;
			}

			var storageAccess = MagicStorage.Instance.GetTile<StorageAccess>();
			var storageHeart = MagicStorage.Instance.GetTile<StorageHeart>();

			const int maxDistance = 200;

			player = player ?? Main.player[Main.myPlayer];

			var portableAccesses = player.inventory.Select(slot => slot.modItem as PortableAccess).Where(slot => slot != null);
			var portableAccessPositions = portableAccesses.Select(access => access.location);

			var searchPositions = GetPossiblePositionsOfAccessTiles(playerPosition.ToTileCoordinates16(), maxDistance);
			var searchPositionsInRange = searchPositions.Where(
				searchPosition => IsAccessInRange(playerPosition, searchPosition, maxDistance));

			var heartPositionsInRange = portableAccessPositions.Concat(searchPositionsInRange)
				.Select(
					position => storageAccess.GetHeart(position.X, position.Y) ?? storageHeart.GetHeart(position.X, position.Y))
				.Where(heart => heart != null);
			var uniqueHeartsInRange = new HashSet<TEStorageHeart>(heartPositionsInRange);

			var heartsWithItem = uniqueHeartsInRange.Where(heart => heart.GetStoredItems()
				.Any(i => ItemData.Matches(i, item) && i.stack > 0));

			foreach (var heart in heartsWithItem)
			{
				var originalStackSize = item.stack;
				heart.DepositItem(item);

				if (Main.netMode == NetmodeID.Server && originalStackSize != item.stack)
				{
					NetHelper.SendRefreshNetworkItems(heart.ID);
				}
			}

			return item;
		}

		// StorageAccess currently doesn't have a ModTileEntity, so we have to search manually for now.
		private static IEnumerable<Point16> GetPossiblePositionsOfAccessTiles(Point16 playerPosition, int maxDistance)
		{
			const int tileWidth = 16;

			// Math.Ceiling requires the division to be done first using floats.
			// We do the calculation manually to avoid converting between floats and ints.
			var candidateRange = (maxDistance + tileWidth - 1) / tileWidth;

			var searchXs = Enumerable.Range(playerPosition.X - candidateRange, candidateRange * 2 + 1).ToArray();
			var searchYs = Enumerable.Range(playerPosition.Y - candidateRange, candidateRange * 2 + 1).ToArray();

			foreach (var searchX in searchXs)
			{
				foreach (var searchY in searchYs)
				{
					yield return new Point16(searchX, searchY);
				}
			}
		}

		public static bool IsAccessInRange(Vector2 playerPosition, Point16 searchPosition, int maxDistance)
		{
			var searchWorldCoords = searchPosition.ToWorldCoordinates();
			var distance = (playerPosition - searchWorldCoords).Length();

			if (distance >= maxDistance)
			{
				return false;
			}

			var tile = Main.tile[searchPosition.X, searchPosition.Y];
			var modTile = TileLoader.GetTile(tile.type);

			return tile.active() && modTile is StorageAccess && !(modTile is CraftingAccess);
		}
	}
}
