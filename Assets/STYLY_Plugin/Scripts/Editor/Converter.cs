using System.IO;
using System;
using System.Net;
using UnityEditor;
using UnityEngine;
using System.Text;
using System.IO.Compression;
using System.Collections.Specialized;
using UnityEngine.Networking;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace STYLY.Uploader
{
	public class Converter
	{
		private UnityEngine.Object asset;
		private string assetPath;
		private string assetTitle;

		private string email;
		private string apiKey;
		private string unityVersion;
		private string unityGuid;

		private bool isAdministrator = false;

		private string stylyGuid;
		private string stylyHash;
		private Dictionary<string, string> signedUrls = new Dictionary<string, string> ();

		public Error error;

		private string renamedAssetPath;

		public Converter (UnityEngine.Object asset)
		{
            this.email = EditorPrefs.GetString(Settings.STYLY_EMAIL);
            this.apiKey = EditorPrefs.GetString(Settings.STYLY_API_KEY);

            isAdministrator = this.email.Equals("info@styly.cc");
            if (isAdministrator)
            {
				this.unityGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(asset));
                // 管理者はスクリプトをあげることがあるので、親オブジェクトを付けない
                this.asset = asset;
                this.assetPath = AssetDatabase.GetAssetPath(this.asset);
                this.assetTitle = System.IO.Path.GetFileNameWithoutExtension(this.assetPath);
                this.unityVersion = UnityEngine.Application.unityVersion;
			} else {
				this.unityGuid = Guid.NewGuid().ToString("D");
                // ユーザー向けにはupload用のオブジェクトを作成して親に空のGameObjectを付ける
                var copy = PrefabUtility.InstantiateAttachedAsset(asset);
                ((GameObject)copy).transform.position = Vector3.zero;
                var uploadPrefab = new GameObject();
                ((GameObject)copy).transform.parent = uploadPrefab.transform;

                if (!AssetDatabase.IsValidFolder("Assets/" + Config.STYLY_TEMP_DIR))
                {
                    AssetDatabase.CreateFolder("Assets", Config.STYLY_TEMP_DIR);
                }

                this.asset = PrefabUtility.CreatePrefab(string.Format(Config.UploadPrefabName, asset.name), uploadPrefab, ReplacePrefabOptions.ConnectToPrefab);
                UnityEditor.AssetDatabase.SaveAssets();
                Editor.DestroyImmediate(uploadPrefab);
                this.assetPath = AssetDatabase.GetAssetPath(this.asset);
                this.assetTitle = System.IO.Path.GetFileNameWithoutExtension(this.assetPath);
                this.unityVersion = UnityEngine.Application.unityVersion;
            }
        }

		public bool BuildAsset ()
		{
			RollbarNotifier.SetUserID (this.email);

			CheckError ();
			if (this.error != null)
				return false;

			GetAzureSignedUrls ();
			if (this.error != null)
				return false;
			
			RenameAssetForBuild ();

			//フォルダのクリーンアップ
			Delete (Config.OutputPath + "STYLY_ASSET");

			CopyPackage ();

			MakeThumbnail ();

			foreach (RuntimePlatform platform in Config.PlatformList) {
				bool result = Build (platform);
				if (result == false) {
					AssetDatabase.MoveAsset (renamedAssetPath, assetPath);
					return false;
				}
			}

			UploadAssets ();
			if (this.error != null)
				return false;

			PostUploadComplete ();
			if (this.error != null)
				return false;

			// Prefab名を戻す
			AssetDatabase.MoveAsset (this.renamedAssetPath, assetPath);
			AssetDatabase.Refresh ();

            if(!isAdministrator)
            {
                UnityEngine.Object.DestroyImmediate(this.asset, true);
            }

            if (isAdministrator) {
				Debug.Log ("[Admin]STYLY_GUID: " + this.stylyGuid);
			}

			DeleteCamera ();

			return true;
		}

		public void CheckError ()
		{
			// unity version check
			bool isUnSupportedVersion = true;
			foreach (string version in Config.UNITY_VERSIONS) {
				if (Application.unityVersion.Contains (version)) {
					isUnSupportedVersion = false;
				}
			}
			if (isUnSupportedVersion) {
				this.error = new Error ("Please change the version of Unity to " + string.Join(", ", Config.UNITY_VERSIONS) + ".");
				return;
			}

			// TODO playmaker version check

			if (this.email == null || this.email.Length == 0
				|| this.asset == null || this.apiKey.Length == 0) {
				this.error = new Error ("You don't have a account settings. [STYLY/Asset Uploader Settings]");
				return;
			}

			//もしSTYLYアセット対応拡張子ではなかったらエラー出して終了
			if (Array.IndexOf (Config.AcceptableExtentions, System.IO.Path.GetExtension (assetPath)) < 0) {
				this.error = new Error ("Unsupported format " + System.IO.Path.GetExtension (assetPath));
				return;
			}

			if (!isAdministrator) {
				GameObject assetObj = AssetDatabase.LoadAssetAtPath (assetPath, typeof(GameObject)) as GameObject;
				Transform[] childTransforms = assetObj.GetComponentsInChildren<Transform> ();
				foreach (Transform t in childTransforms) {
					//禁止アセットのチェック
					if (Config.ProhibitedTags.Contains (t.gameObject.tag)) {
						this.error = new Error (string.Format ("{0} can not be used as a tag.", t.tag));
						return;
					}
					//禁止レイヤーのチェック
					//Defaultレイヤー以外使用できない
					if (t.gameObject.layer != 0) {
						this.error = new Error ("You can not use anything other than the Default layer. You use " + LayerMask.LayerToName (t.gameObject.layer));
						return;
					}

					//禁止コンポーネントのチェック
					foreach (string component in Config.ProhibitedComponents) {
						if (t.gameObject.GetComponent (component)) {
							this.error = new Error (string.Format ("{0} can not be used as a component.", component));
							return;
						}
					}

					//名前空間チェック
					Component[] components = t.gameObject.GetComponents (typeof(Component));
					foreach (Component c in components) {
						bool usable = false;
						foreach (string nameSpace in Config.CanUseNameSpaces) {
							if (c.GetType ().FullName.StartsWith (nameSpace)) {
								usable = true;
							}
						}
						if (!usable) {
							this.error = new Error (string.Format ("You can not use your own script. {0}", c.GetType ().FullName));
							return;
						}
					}

                    // Haloチェック
                    var ts = t.GetComponentsInChildren<Transform>();
                    for (int i = 0; i < ts.Length; i++)
                    {
                        var halo = (Behaviour)ts[i].gameObject.GetComponent("Halo");
                        if (halo != null)
                        {
                            this.error = new Error("Since Halo does not work with WebGL, it can not be used.");
                            return;
                        }
                    }
                }
			}
		}

		public void GetAzureSignedUrls ()
		{
			NameValueCollection form = new NameValueCollection ();
			form ["bu_email"] = this.email;
			form ["bu_api_key"] = this.apiKey;
            form["sa_unity_version"] = this.unityVersion;
			form ["sa_unity_guid"] = this.unityGuid;

			try {
                
                ServicePointManager.Expect100Continue = true;
                ServicePointManager.CheckCertificateRevocationList = false;
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls;
                ServicePointManager.ServerCertificateValidationCallback = RemoteCertificateValidationCallback;

                var wb = new WebClientWithTimeout ();
				var response = wb.UploadValues (Config.AzureSignedUrls, "POST", form);
				string responseString = Encoding.ASCII.GetString (response).Trim ();

				Dictionary<string, object> dict = Json.Deserialize (responseString) as Dictionary<string, object>;
				this.stylyGuid = (string)dict ["styly_guid"];
				this.stylyHash = (string)dict ["styly_hash"];
				this.signedUrls.Add ("Package", (string)dict ["url_package"]);
				this.signedUrls.Add ("Thumbnail", (string)dict ["url_thumbnail"]);
				Dictionary<string, object> platforms = (Dictionary<string, object>)dict ["platforms"];
				this.signedUrls.Add ("Android", (string)platforms ["Android"]);
				this.signedUrls.Add ("iOS", (string)platforms ["iOS"]);
				this.signedUrls.Add ("OSX", (string)platforms ["OSX"]);
				this.signedUrls.Add ("Windows", (string)platforms ["Windows"]);
				this.signedUrls.Add ("WebGL", (string)platforms ["WebGL"]);

                if (isAdministrator)
                {
                    Debug.Log("[Admin]" + Json.Serialize(this.signedUrls));
                }
			} catch (Exception e) {
				this.error = new Error ("Authentication failed.\nPlease check your email and api key is valid.\n" + e.Message);
			}
		}

		//Prefab名をSTYLY GUIDに一時的に変更
		private void RenameAssetForBuild ()
		{
			this.renamedAssetPath = System.IO.Path.GetDirectoryName (assetPath) + "/" + stylyGuid + System.IO.Path.GetExtension (assetPath);
			AssetDatabase.MoveAsset (assetPath, this.renamedAssetPath);
			AssetDatabase.SaveAssets ();
			AssetDatabase.Refresh ();
		}

		private void CopyPackage ()
		{
			//パッケージ形式でバックアップ用にExport
			if (!Directory.Exists (Config.OutputPath + "/STYLY_ASSET/Packages/"))
				Directory.CreateDirectory (Config.OutputPath + "/STYLY_ASSET/Packages/");

			var exportPackageFile = Config.OutputPath + "/STYLY_ASSET/Packages/" + this.stylyGuid + ".unitypackage";

			// これを入れないとMacだと落ちる。
			// なぜなら、AssetDatabase.ExportPackage メソッドなどがプログレスウィンドウを表示しようとするので、すでにプログレスウィンドウがあると問題になる模様。
			EditorUtility.ClearProgressBar ();

			AssetDatabase.ExportPackage (this.renamedAssetPath, exportPackageFile, ExportPackageOptions.IncludeDependencies);
		}

		private void MakeThumbnail ()
		{
			if (!Directory.Exists (Config.OutputPath + "/STYLY_ASSET/Thumbnails/"))
				Directory.CreateDirectory (Config.OutputPath + "/STYLY_ASSET/Thumbnails/");

			GameObject targetObj = AssetDatabase.LoadAssetAtPath (this.renamedAssetPath, typeof(GameObject)) as GameObject;
			GameObject unit = UnityEngine.Object.Instantiate (targetObj, Vector3.zero, Quaternion.identity) as GameObject;
			string thumbnailPath = System.IO.Directory.GetParent (Application.dataPath) + "/" + Config.OutputPath + "/STYLY_ASSET/Thumbnails/" + this.stylyGuid + ".png";
			Thumbnail.MakeThumbnail (unit, thumbnailPath, Config.ThumbnailWidth, Config.ThumbnailHeight);
		}

		/// アップロード後にサーバーに通知
		private void PostUploadComplete ()
		{
			try {
				NameValueCollection form = new NameValueCollection ();
				form ["bu_email"] = this.email;
				form ["bu_api_key"] = this.apiKey;
				form ["sa_unity_version"] = this.unityVersion;
				form ["sa_unity_guid"] = this.unityGuid;
				form ["sa_title"] = this.assetTitle;
				form ["styly_hash"] = this.stylyHash;
				form ["styly_guid"] = this.stylyGuid;

				var wb = new WebClientWithTimeout ();
				wb.UploadValues (Config.StylyAssetUploadCompleteUrl, "POST", form);
			} catch (Exception e) {
				this.error = new Error (e.Message);
			}
		}

		public bool UploadAssets ()
		{
			string AssetBundlesOutputPath = Config.OutputPath + "STYLY_ASSET";

			bool uploadResult = true;
			uploadResult = UploadToStorage (this.signedUrls ["Package"],
				System.IO.Directory.GetParent (Application.dataPath) + "/" + AssetBundlesOutputPath + "/Packages/" + this.stylyGuid + ".unitypackage");
        
			if (uploadResult == false) {
				return false;
			}
				
			uploadResult = UploadToStorage (this.signedUrls ["Thumbnail"],
				System.IO.Directory.GetParent (Application.dataPath) + "/" + AssetBundlesOutputPath + "/Thumbnails/" + this.stylyGuid + ".png");
        	
			if (uploadResult == false) {
				return false;
			}

			foreach (var platform in Config.PlatformList) {
				string platformString = GetPlatformName (platform);
				uploadResult = UploadToStorage (this.signedUrls [platformString], 
					System.IO.Directory.GetParent (Application.dataPath) + "/" + AssetBundlesOutputPath + "/" + platformString + "/" + this.stylyGuid);
				if (uploadResult == false) {
					return false;
				}
			}
			return true;
		}


		private bool UploadToStorage (string url, string filePath)
		{
			try {
				byte[] myData = LoadBinaryData (filePath);

				ServicePointManager.Expect100Continue = true;
				ServicePointManager.CheckCertificateRevocationList = false;
				ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls;
				ServicePointManager.ServerCertificateValidationCallback = RemoteCertificateValidationCallback;
	            
				HttpWebRequest client = WebRequest.Create (url) as HttpWebRequest;
				client.Timeout = 10 * 60 * 1000;
	            client.KeepAlive = true;
				client.Method = "PUT";
				client.ContentLength = myData.Length;
				client.Credentials = CredentialCache.DefaultCredentials;
	            client.Headers.Add ("x-ms-blob-type", "BlockBlob");
	            client.Headers.Add ("x-ms-date", DateTime.UtcNow.ToString("R", System.Globalization.CultureInfo.InvariantCulture));

	            using (Stream requestStream = client.GetRequestStream ()) {
	                requestStream.Write (myData, 0, myData.Length);
				}

				using (HttpWebResponse res = client.GetResponse () as HttpWebResponse) {
	                bool isOK = true;
					if (res.StatusCode != HttpStatusCode.Created) {
						isOK = false;
					}
					using (StreamReader reader = new StreamReader (res.GetResponseStream ())) {
	                    string data = reader.ReadToEnd ();
						if (!isOK) {
							this.error = new Error (data, new Dictionary<string, string> {
								{ "url", url },
								{ "file_path", filePath }
							});
							return false;
						}
					}
				}
			} catch (Exception e) {
				this.error = new Error (e);
				return false;
			}
			return true;
		}

		private static bool RemoteCertificateValidationCallback (object sender,
		                                                         System.Security.Cryptography.X509Certificates.X509Certificate certificate,
		                                                         System.Security.Cryptography.X509Certificates.X509Chain chain,
		                                                         System.Net.Security.SslPolicyErrors sslPolicyErrors)
		{
			return true;
		}


		static byte[] LoadBinaryData (string path)
		{
			FileStream fs = null;
			try {
				fs = new FileStream (path, FileMode.Open);
			} catch (FileNotFoundException e) {
				if (path.IndexOf ("/Thumbnails/") >= 0) {
					fs = new FileStream (Application.dataPath + "/_STYLY_AssetUploader/Resources/dummy_thumbnail.png", FileMode.Open);
				} else {
					throw e;
				}
			}

			BinaryReader br = new BinaryReader (fs);
			byte[] buf = br.ReadBytes ((int)br.BaseStream.Length);
			br.Close ();
			return buf;
		}


		public bool Build (RuntimePlatform platform)
		{
			//対象プラットフォームごとに出力フォルダ作成
			string outputPath = Path.Combine (Config.OutputPath + "STYLY_ASSET", GetPlatformName (platform));
			if (!Directory.Exists (outputPath)) {
				Directory.CreateDirectory (outputPath);
			}
          	
			bool switchResult = true;

			if (platform == RuntimePlatform.WindowsPlayer) {
				// プラットフォームとGraphic APIを常に同じにする
//#if UNITY_5_6
				switchResult = EditorUserBuildSettings.SwitchActiveBuildTarget (BuildTargetGroup.Standalone, BuildTarget.StandaloneWindows64);
//#else
//                EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTarget.StandaloneWindows64);
//#endif
				PlayerSettings.colorSpace = ColorSpace.Gamma;
				PlayerSettings.SetUseDefaultGraphicsAPIs (BuildTarget.StandaloneWindows64, false);
				PlayerSettings.SetGraphicsAPIs (BuildTarget.StandaloneWindows64, new UnityEngine.Rendering.GraphicsDeviceType[] {
					UnityEngine.Rendering.GraphicsDeviceType.Direct3D9,
					UnityEngine.Rendering.GraphicsDeviceType.Direct3D11
				});
			} else if (platform == RuntimePlatform.Android) {
//#if UNITY_5_6
				switchResult = EditorUserBuildSettings.SwitchActiveBuildTarget (BuildTargetGroup.Android, BuildTarget.Android);
//#else
//                EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTarget.Android);
//#endif
				EditorUserBuildSettings.androidBuildSystem = AndroidBuildSystem.Gradle;

				PlayerSettings.colorSpace = ColorSpace.Gamma;
				PlayerSettings.SetUseDefaultGraphicsAPIs (BuildTarget.Android, false);
				PlayerSettings.SetGraphicsAPIs (BuildTarget.Android, new UnityEngine.Rendering.GraphicsDeviceType[] {
					UnityEngine.Rendering.GraphicsDeviceType.OpenGLES2,
					UnityEngine.Rendering.GraphicsDeviceType.OpenGLES3
				});
			} else if (platform == RuntimePlatform.IPhonePlayer) {
//#if UNITY_5_6
				switchResult = EditorUserBuildSettings.SwitchActiveBuildTarget (BuildTargetGroup.iOS, BuildTarget.iOS);
//#else
//                EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTarget.iOS);
//#endif
				PlayerSettings.colorSpace = ColorSpace.Gamma;
				PlayerSettings.SetUseDefaultGraphicsAPIs (BuildTarget.iOS, false);
				PlayerSettings.SetGraphicsAPIs (BuildTarget.iOS, new UnityEngine.Rendering.GraphicsDeviceType[] {
					UnityEngine.Rendering.GraphicsDeviceType.OpenGLES2,
					UnityEngine.Rendering.GraphicsDeviceType.Metal
				});
			} else if (platform == RuntimePlatform.OSXPlayer) {
//#if UNITY_5_6
				switchResult = EditorUserBuildSettings.SwitchActiveBuildTarget (BuildTargetGroup.Standalone, BuildTarget.StandaloneOSXIntel64);
//#else
//                EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTarget.StandaloneOSXIntel64);
//#endif
				PlayerSettings.colorSpace = ColorSpace.Gamma;
				PlayerSettings.SetUseDefaultGraphicsAPIs (BuildTarget.StandaloneOSXIntel64, false);
				PlayerSettings.SetGraphicsAPIs (BuildTarget.StandaloneOSXIntel64, new UnityEngine.Rendering.GraphicsDeviceType[] {
					UnityEngine.Rendering.GraphicsDeviceType.OpenGLES2,
					UnityEngine.Rendering.GraphicsDeviceType.Metal
				});
			} else if (platform == RuntimePlatform.WebGLPlayer) {
//#if UNITY_5_6
				switchResult = EditorUserBuildSettings.SwitchActiveBuildTarget (BuildTargetGroup.WebGL, BuildTarget.WebGL);
//#else
//                EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTarget.WebGL);
//#endif
				PlayerSettings.colorSpace = ColorSpace.Gamma;
				PlayerSettings.SetUseDefaultGraphicsAPIs (BuildTarget.WebGL, true);
				// web gl 1.0, web gl 2.0 がUnityEngine.Rendering.GraphicsDeviceTypeにないからautoで設定している
			}

			if (switchResult == false) {
				this.error = new Error ("Can not switch Build target to " + GetPlatformName (platform) + ".\n"
				+ "Make sure you have installed the target build module.\n"
				+ "This tool requires Android, iOS, OSX, WebGL, Windows platforms.");
				return false;
			} 
            
			AssetBundleBuild[] buildMap = new AssetBundleBuild[1];
			buildMap [0].assetBundleName = this.stylyGuid;
			buildMap [0].assetNames = new string[] { this.renamedAssetPath };

			AssetBundleManifest buildResult = BuildPipeline.BuildAssetBundles (outputPath, buildMap, BuildAssetBundleOptions.ChunkBasedCompression, GetBuildTarget (platform));
			if (buildResult == null) {
				this.error = new Error ("Buid asset bundle failed for platform " + GetPlatformName (platform));
				return false;
			}
			return true;
		}

		private void DeleteCamera() {
			UnityEditor.EditorApplication.delayCall += () => {
				var deleteCamera = GameObject.Find (Thumbnail.STYLY_Thumbnail_Camera_Name);
				if (deleteCamera) {
					UnityEngine.Object.DestroyImmediate (deleteCamera);
				}
				var deleteObject = GameObject.Find (Thumbnail.STYLY_Thumbnail_Object_Name);
				if (deleteObject) {
					UnityEngine.Object.DestroyImmediate (deleteObject);
				}
			};
		}

		public BuildTarget GetBuildTarget (RuntimePlatform platform)
		{
			switch (platform) {
			case RuntimePlatform.Android:
				return BuildTarget.Android;
			case RuntimePlatform.IPhonePlayer:
				return BuildTarget.iOS;
			case RuntimePlatform.WebGLPlayer:
				return BuildTarget.WebGL;
			case RuntimePlatform.WindowsPlayer:
				return BuildTarget.StandaloneWindows64;
			case RuntimePlatform.OSXPlayer:
				return BuildTarget.StandaloneOSXIntel64;
			default:
				return BuildTarget.StandaloneWindows64;
			}
		}


		public string GetPlatformName (RuntimePlatform platform)
		{
			switch (platform) {
			case RuntimePlatform.Android:
				return "Android";
			case RuntimePlatform.IPhonePlayer:
				return "iOS";
			case RuntimePlatform.WebGLPlayer:
				return "WebGL";
			case RuntimePlatform.WindowsEditor:
			case RuntimePlatform.WindowsPlayer:
				return "Windows";
			case RuntimePlatform.OSXPlayer:
			case RuntimePlatform.OSXEditor:
				return "OSX";
			default:
				return null;
			}
		}


		/// 指定したディレクトリとその中身を全て削除する
		/// http://kan-kikuchi.hatenablog.com/entry/DirectoryProcessor
		public static void Delete (string targetDirectoryPath)
		{
			if (!Directory.Exists (targetDirectoryPath)) {
				return;
			}

			//ディレクトリ以外の全ファイルを削除
			string[] filePaths = Directory.GetFiles (targetDirectoryPath);
			foreach (string filePath in filePaths) {
				File.SetAttributes (filePath, FileAttributes.Normal);
				File.Delete (filePath);
			}

			//ディレクトリの中のディレクトリも再帰的に削除
			string[] directoryPaths = Directory.GetDirectories (targetDirectoryPath);
			foreach (string directoryPath in directoryPaths) {
				Delete (directoryPath);
			}

			//中が空になったらディレクトリ自身も削除
			Directory.Delete (targetDirectoryPath, false);
		}


	}
}
