﻿#if AK_WWISE_ADDRESSABLES && UNITY_ADDRESSABLES

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

using UnityEditor;
using UnityEngine;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;

namespace AK.Wwise.Unity.WwiseAddressables
{
	public class WwiseBankPostSaveProcessor : UnityEditor.AssetModificationProcessor
	{
		//Clear parsed soundbank info after updating assets (force reparse when new bnk and wem assets are added)
		static string[] OnWillSaveAssets(string[] paths)
		{
			bool wwiseAssetsModified = false;
			foreach (var item in paths)
			{
				if (Path.GetExtension(item) == ".bnk")
				{
					wwiseAssetsModified = true;
					break;
				}
				else if (Path.GetExtension(item) == ".wem")
				{
					wwiseAssetsModified = true;
					break;
				}
			}

			if (wwiseAssetsModified)
			{
				AkAddressablesEditorUtilities.ClearSoundbankInfo();
			}

			return paths;
		}
	}

	public class WwiseBankPostProcess : AssetPostprocessor
	{
		public delegate bool GetAssetMetadata(string name, string platform, string language, ref AddressableMetadata addressableMetadata);
		
		//Bind to this delegate to customize grouping or labeling of Wwise bank and media assets
		public static GetAssetMetadata GetAssetMetadataDelegate;

		static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets, string[] movedAssets, string[] movedFromAssetPaths)
		{
			UpdateAssetReferences(importedAssets);
			RemoveAssetReferences(deletedAssets);
		}

		public static void UpdateAssetReferences(string[] assets)
		{
			HashSet<string> bankAssetsToProcess = new HashSet<string>();
			HashSet<string> streamingAssetsToProcess = new HashSet<string>();

			foreach (var item in assets)
			{
				if (Path.GetExtension(item) == ".bnk")
				{
					bankAssetsToProcess.Add(item);
				}

				if (Path.GetExtension(item) == ".wem")
				{
					streamingAssetsToProcess.Add(item);
				}
			}

			if (bankAssetsToProcess.Count > 0)
			{
				ConcurrentDictionary<string, WwiseAddressableSoundBank> addressableAssetCache =
					new ConcurrentDictionary<string, WwiseAddressableSoundBank>();
				AddBankReferenceToAddressableBankAsset(addressableAssetCache, bankAssetsToProcess);
				AddAssetsToAddressablesGroup(bankAssetsToProcess);
			}

			if (streamingAssetsToProcess.Count > 0)
			{
				AddStreamedAssetsToBanks(streamingAssetsToProcess);
				AddAssetsToAddressablesGroup(streamingAssetsToProcess);
			}
		}

		public static void RemoveAssetReferences(string[] deletedAssets)
		{
			HashSet<string> bankAssetsToProcess = new HashSet<string>();
			HashSet<string> streamingAssetsToProcess = new HashSet<string>();

			foreach (var item in deletedAssets)
			{
				if (Path.GetExtension(item) == ".bnk")
				{
					bankAssetsToProcess.Add(item);
				}

				if (Path.GetExtension(item) == ".wem")
				{
					streamingAssetsToProcess.Add(item);
				}
			}

			if (streamingAssetsToProcess.Count > 0)
			{
				RemoveStreamedAssetsFromBanks(streamingAssetsToProcess);
				RemoveAssetsFromAddressables(streamingAssetsToProcess);
			}

			if (bankAssetsToProcess.Count > 0)
			{
				RemoveBanksFromAddressableSoundbanks(bankAssetsToProcess);
				RemoveAssetsFromAddressables(bankAssetsToProcess);
			}
		}


		struct CreateAssetEntry
		{
			public WwiseAddressableSoundBank Asset;
			public string Path;
			public string Name;
		}

		internal static AddressableAssetGroup GetOrAddGroup(AddressableAssetSettings settings, string groupName)
		{
			AddressableAssetGroup group = settings.groups.Find(x => x.Name == groupName);
			if (group == null)
			{
				group = settings.CreateGroup(groupName, false, false, false, new List<AddressableAssetGroupSchema> { settings.DefaultGroup.Schemas[0] }, typeof(BundledAssetGroupSchema), typeof(ContentUpdateGroupSchema));
				var bundleSchema = group.GetSchema<BundledAssetGroupSchema>();
				if (bundleSchema != null)
				{
					bundleSchema.Compression = BundledAssetGroupSchema.BundleCompressionMode.Uncompressed;
					bundleSchema.BuildPath.SetVariableByName(settings, AddressableAssetSettings.kRemoteBuildPath);
					bundleSchema.LoadPath.SetVariableByName(settings, AddressableAssetSettings.kRemoteLoadPath);
					bundleSchema.BundleMode = BundledAssetGroupSchema.BundlePackingMode.PackSeparately;
				}

				var contentUpdateSchema = group.GetSchema<ContentUpdateGroupSchema>();
				contentUpdateSchema.StaticContent = false;
			}

			return group;
		}

		internal static void AddBankReferenceToAddressableBankAsset(ConcurrentDictionary<string, WwiseAddressableSoundBank> addressableAssetCache, HashSet<string> bankAssetsAdded)
		{
			List<CreateAssetEntry> itemsToCreate = new List<CreateAssetEntry>();
			try
			{

				foreach (var bankPath in bankAssetsAdded)
				{
					string name = Path.GetFileNameWithoutExtension(bankPath);

					string platform;
					string language;
					AkAddressablesEditorUtilities.ParseAssetPath(bankPath, out platform, out language);

					// First find or create AddressableBank asset
					WwiseAddressableSoundBank addressableBankAsset = null;
					if (!addressableAssetCache.TryGetValue(name, out addressableBankAsset))
					{
						var results = AssetDatabase.FindAssets(string.Format("{0} t:{1}", name, nameof(WwiseAddressableSoundBank)));
						if (results.Length > 0)
						{
							string addressableBankGuid = results.First();
							string addressableBankPath = AssetDatabase.GUIDToAssetPath(addressableBankGuid);
							addressableBankAsset = AssetDatabase.LoadAssetAtPath<WwiseAddressableSoundBank>(addressableBankPath);
						}

						string addressableBankAssetDirectory = AkAssetUtilities.GetSoundbanksPath();
						string addressableBankAssetPath = string.Format("{0}/{1}", addressableBankAssetDirectory, Path.ChangeExtension(name, ".asset"));
						if (addressableBankAsset == null)
						{
							if (!AssetDatabase.IsValidFolder(addressableBankAssetDirectory))
							{
								StringBuilder currentPathBuilder = new StringBuilder();
								var addressableBankAssetParts = addressableBankAssetDirectory.Split('/');

								currentPathBuilder.Append(addressableBankAssetParts[0]);

								for (int i = 1; i < addressableBankAssetParts.Length; ++i)
								{
									string previousPath = currentPathBuilder.ToString();

									currentPathBuilder.AppendFormat("/{0}", addressableBankAssetParts[i]);

									string currentPath = currentPathBuilder.ToString();

									if (!AssetDatabase.IsValidFolder(currentPath))
									{
										AssetDatabase.CreateFolder(previousPath, addressableBankAssetParts[i]);
									}
								}
							}
							addressableBankAsset = ScriptableObject.CreateInstance<WwiseAddressableSoundBank>();
							itemsToCreate.Add(new CreateAssetEntry { Asset = addressableBankAsset, Path = addressableBankAssetPath, Name = name });

						}
						else
						{
							UpdateAddressableBankReference(addressableBankAsset, name);
						}

						addressableAssetCache.AddOrUpdate(name, addressableBankAsset, (key, oldValue) => addressableBankAsset);

						if (name == "Init")
						{
							var wwiseGlobablGameObject = GameObject.Find("WwiseGlobal");
							InitBankHolder bankHolder;
							if (wwiseGlobablGameObject!= null)
							{
								bankHolder = wwiseGlobablGameObject.GetComponent<InitBankHolder>();
								if (bankHolder == null)
								{
									wwiseGlobablGameObject.AddComponent<InitBankHolder>();
									bankHolder = wwiseGlobablGameObject.GetComponent<InitBankHolder>();
								}
								bankHolder.InitBank = addressableBankAsset;
							}
							else
							{
								bankHolder = UnityEngine.Object.FindObjectOfType<InitBankHolder>();
								if (bankHolder != null)
								{
									bankHolder.InitBank = addressableBankAsset;
								}
								else
								{
									Debug.Log("Could not find or create the InitBankHolder, you will have to add one to the WwiseGlobal object in your scene and ensure it references the Init bank.");
								}
							}
						}
					}

					if (!string.IsNullOrEmpty(platform))
					{
						var soundbankInfos = AkAddressablesEditorUtilities.ParsePlatformSoundbanksXML(platform, name);
						if (soundbankInfos.ContainsKey(name))
						{
							addressableBankAsset.UpdateLocalizationLanguages(platform, soundbankInfos[name].Keys.ToList());
							addressableBankAsset.AddOrUpdate(platform, language, new AssetReferenceWwiseBankData(AssetDatabase.AssetPathToGUID(bankPath)));
							EditorUtility.SetDirty(addressableBankAsset);
						}
						else
						{
							Debug.LogWarning($"Could not update {addressableBankAsset.name} with bank located at {bankPath}");
						}

					}
				}
			}
			finally
			{
				if (itemsToCreate.Count > 0)
				{
					AssetDatabase.StartAssetEditing();
					foreach (var entry in itemsToCreate)
					{
						AssetDatabase.CreateAsset(entry.Asset, entry.Path);
						UpdateAddressableBankReference(entry.Asset, entry.Name);
					}
					AssetDatabase.StopAssetEditing();
				}

				AssetDatabase.SaveAssets();
				AssetDatabase.Refresh();
			}
		}
		internal static void UpdateAddressableBankReference(WwiseAddressableSoundBank asset, string bankName )
		{
			bool bankScriptableObjectUpdated = false;
			if (AkAssetUtilities.AddressableBankUpdated != null)
			{
				bankScriptableObjectUpdated = AkAssetUtilities.AddressableBankUpdated.GetInvocationList().Select(x => (bool)x.DynamicInvoke(asset, bankName)).Any(v => v);
			}
			if (!bankScriptableObjectUpdated)
			{
				AkAddressablesEditorUtilities.FindAndSetBankReference(asset, bankName);
			}
		}

		internal static void AddStreamedAssetsToBanks(HashSet<string> streamingAssetsAdded)
		{
			try
			{
				var updatedBanks = new List<string>();
				foreach (var assetPath in streamingAssetsAdded)
				{
					string name = Path.GetFileNameWithoutExtension(assetPath);

					string platform;
					string language;
					AkAddressablesEditorUtilities.ParseAssetPath(assetPath, out platform, out language);

					var soundbankInfos = AkAddressablesEditorUtilities.ParsePlatformSoundbanksXML(platform, name);

					if (!soundbankInfos.eventToSoundBankMap.ContainsKey(name) && assetPath.Contains("ExternalSources")) continue;

					var bankNames = soundbankInfos.eventToSoundBankMap[name];
					foreach (var bankName in bankNames)
					{
						string bankAssetDir = Path.GetDirectoryName(assetPath);
						string addressableBankAssetDir = AkAssetUtilities.GetSoundbanksPath();
						string addressableBankAssetPath = System.IO.Path.Combine(addressableBankAssetDir, bankName + ".asset");
						var bankAsset = AssetDatabase.LoadAssetAtPath<WwiseAddressableSoundBank>(addressableBankAssetPath);

						if (bankAsset == null)
						{
							continue;
						}

						if (!string.IsNullOrEmpty(platform))
						{
							if (!soundbankInfos[bankName].TryGetValue(language, out AkAddressablesEditorUtilities.SoundBankInfo sbInfo))
							{
								if (int.TryParse(language, out int result))
									UnityEngine.Debug.LogError("Wwise Unity Addressables: Sub-folders for generated files currently not supported. Please turn off the option in Wwise under Project Settings -> SoundBanks");
								else
									UnityEngine.Debug.LogError($"Wwise Unity Addressables: Unable to process asset at path {assetPath}: Unrecognized language {language}");
								continue;
							}
							List<string> MediaIds = sbInfo.streamedFileIds;
							bankAsset.UpdateLocalizationLanguages(platform, soundbankInfos[bankName].Keys.ToList());
							bankAsset.SetStreamingMedia(platform, language, bankAssetDir, MediaIds);
							EditorUtility.SetDirty(bankAsset);
						}
					}
				}
			}
			finally
			{
				AssetDatabase.SaveAssets();
				AssetDatabase.Refresh();
			}
		}

		internal static void RemoveStreamedAssetsFromBanks(HashSet<string> streamingAssetsToRemove)
		{
			try
			{
				var foundBanks = AssetDatabase.FindAssets($"t:{typeof(WwiseAddressableSoundBank).Name}");
				var updatedBanks = new List<string>();
				foreach (var assetPath in streamingAssetsToRemove)
				{
					string platform;
					string language;
					AkAddressablesEditorUtilities.ParseAssetPath(assetPath, out platform, out language);
					var assetGuid = AssetDatabase.AssetPathToGUID(assetPath);

					foreach (var bankGuid in foundBanks)
					{
						var bankPath = AssetDatabase.GUIDToAssetPath(bankGuid);
						var bank = AssetDatabase.LoadAssetAtPath<WwiseAddressableSoundBank>(bankPath);
						if (bank.TryRemoveMedia(platform, language, assetGuid))
						{
							EditorUtility.SetDirty(bank);
						}
					}
				}
			}
			finally
			{
				AssetDatabase.SaveAssets();
				AssetDatabase.Refresh();
			}
		}

		internal static void RemoveBanksFromAddressableSoundbanks(HashSet<string> bankAssetsToRemove)
		{
			try
			{
				var foundBanks = AssetDatabase.FindAssets($"t:{typeof(WwiseAddressableSoundBank).Name}");
				var updatedBanks = new List<string>();
				foreach (var assetPath in bankAssetsToRemove)
				{
					string platform;
					string language;
					AkAddressablesEditorUtilities.ParseAssetPath(assetPath, out platform, out language);
					var assetGuid = AssetDatabase.AssetPathToGUID(assetPath);

					foreach (var bankGuid in foundBanks)
					{
						var bankPath = AssetDatabase.GUIDToAssetPath(bankGuid);
						var bank = AssetDatabase.LoadAssetAtPath<WwiseAddressableSoundBank>(bankPath);
						if (bank.TryRemoveBank(platform, language, assetGuid))
						{
							EditorUtility.SetDirty(bank);
							break;
						}
					}
				}
			}
			finally
			{
				AssetDatabase.SaveAssets();
				AssetDatabase.Refresh();
			}
		}

		internal static void AddAssetsToAddressablesGroup(HashSet<string> assetsAdded, string groupName = "")
		{
			if (AddressableAssetSettingsDefaultObject.Settings == null)
			{
				AddressableAssetSettingsDefaultObject.Settings =
					AddressableAssetSettings.Create(AddressableAssetSettingsDefaultObject.kDefaultConfigFolder,
					AddressableAssetSettingsDefaultObject.kDefaultConfigAssetName, true, true);
			}

			var settings = AddressableAssetSettingsDefaultObject.Settings;
			if (settings == null)
			{
				Debug.LogWarningFormat("[Addressables] settings file not found.\nPlease go to Menu/Window/Asset Management/Addressables/Groups, then click 'Create Addressables Settings' button.");
				return;
			}
			List<AddressableAssetEntry> groupEntriesModified = new List<AddressableAssetEntry>();
			var parseGroupNames = string.IsNullOrEmpty(groupName);
			foreach (var assetPath in assetsAdded)
			{
				string guid = AssetDatabase.AssetPathToGUID(assetPath);

				string platform;
				string language;
				AkAddressablesEditorUtilities.ParseAssetPath(assetPath, out platform, out language);
				AddressableMetadata assetMetadata = ScriptableObject.CreateInstance<AddressableMetadata>();

				if (parseGroupNames)
				{
					if (string.IsNullOrEmpty(platform))
					{
						Debug.LogError($"Wwise Addressables import : could not parse platform for {assetPath}. It will not be made addressable.");
						continue;
					}
					if (GetAssetMetadataDelegate != null)
					{
						if (!GetAssetMetadataDelegate.Invoke(Path.GetFileName(assetPath), platform, language, ref assetMetadata))
						{
							// If we can't find a metadata asset use the default group name
							assetMetadata.groupName = GetDefaultAddressableGroup(Path.GetFileName(assetPath), platform);
						}
					}
					else
					{
						assetMetadata.groupName = GetDefaultAddressableGroup(Path.GetFileName(assetPath), platform);
					}
				}
				else
				{
					assetMetadata.groupName = groupName;
				}

				AddressableAssetGroup group = GetOrAddGroup(settings, assetMetadata.groupName);
				var groupEntry = settings.CreateOrMoveEntry(guid, group);
				if (groupEntry != null)
				{
					/*if (assetMetadata.labels.Count >0)
					{
						foreach (string label in assetMetadata.labels)
						{
							groupEntry.labels.Add(label);
						}
					}*/
					groupEntry.labels.Add("Remote_Assets");
					groupEntry.labels.Add("Remote_Sounds");
					groupEntriesModified.Add(groupEntry);
				}
			}

			if (groupEntriesModified.Count > 0)
			{
				settings.SetDirty(AddressableAssetSettings.ModificationEvent.EntryMoved, groupEntriesModified, true);
				AssetDatabase.SaveAssets();
			}
		}

		static string  GetDefaultAddressableGroup(string assetName, string platform)
		{
			string groupName = $"WwiseData_{platform}";
			if (assetName == "Init.bnk")
			{
				groupName = $"WwiseData_{platform}_InitBank";
			}
			return groupName;
		}

		internal static void RemoveAssetsFromAddressables(HashSet<string> assetsToRemove)
		{
			if (AddressableAssetSettingsDefaultObject.Settings == null)
			{
				return;
			}

			var settings = AddressableAssetSettingsDefaultObject.Settings;
			if (settings == null)
			{
				Debug.LogWarningFormat("[Addressables] settings file not found.\nPlease go to Menu/Window/Asset Management/Addressables/Groups, then click 'Create Addressables Settings' button.");
				return;
			}

			foreach (var assetPath in assetsToRemove)
			{
				string guid = AssetDatabase.AssetPathToGUID(assetPath);
				var assetEntry = settings.FindAssetEntry(guid);
				if (assetEntry == null)
				{
					return;
				}
				var parentGroup = assetEntry.parentGroup;
				settings.RemoveAssetEntry(guid);
			}
		}
	}
}
#endif // AK_WWISE_ADDRESSABLES
