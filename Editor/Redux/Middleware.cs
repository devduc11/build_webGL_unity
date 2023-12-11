using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Unity.EditorCoroutines.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace Unity.Play.Publisher.Editor
{
    public class PublisherMiddleware
    {
        const string WebglSharingFile = "webgl_sharing";
        const string ZipName = "connectwebgl.zip";
        const string UploadEndpoint = "/upload_from_form/";
        const string QueryProgressEndpoint = "/api/webgl/progress";
        const string UndefinedGUID = "UNDEFINED_GUID";
        const int ZipFileLimitBytes = 200 * 1024 * 1024;

        static EditorCoroutine waitUntilUserLogsInRoutine;
        static UnityWebRequest uploadRequest;

        public static void ZipAndPublish(string title, string buildPath)
        {
            if (!PublisherUtils.BuildIsValid(buildPath))
            {
                Debug.LogError("Invalid build path.");
                return;
            }

            if (!Zip(buildPath)) { return; }
            string GUIDPath = Path.Combine(buildPath, "GUID.txt");
            if (File.Exists(GUIDPath))
            {
                Upload(File.ReadAllText(GUIDPath));
                return;
            }
            Debug.LogWarningFormat("Missing GUID file for {0}, consider deleting the build and making a new one through the WebGL Publisher", buildPath);
            Upload(UndefinedGUID);
        }

        static bool Zip(string buildOutputDir)
        {
            var projectDir = Directory.GetParent(Application.dataPath).FullName;
            var destPath = Path.Combine(projectDir, ZipName);

            File.Delete(destPath);

            ZipFile.CreateFromDirectory(buildOutputDir, destPath);
            FileInfo fileInfo = new FileInfo(destPath);

            if (fileInfo.Length > ZipFileLimitBytes)
            {
                Debug.LogError("Zip file exceeds the size limit.");
                return false;
            }

            return true;
        }

        static void Upload(string buildGUID)
        {
            // Thay đổi host và token tại đây
            string host = "https://games.taapgame.com";
            string access_token = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJyb2xlIjoiZGV2IiwiYXV0aG9yIjoiZGV2IiwiaWF0IjoxNjU1NjkxNjAwLCJleHAiOjI1MTk2OTE2MDB9.7rOZjFaqs2U3RZESisQIrnrh9IJ3QWcTtAINqEdhTqQ";

            string baseUrl = $"{host}{UploadEndpoint}";
            var formSections = new List<IMultipartFormSection>
            {
                new MultipartFormDataSection("title", "YourDefaultTitle"), // Set your default title here
                new MultipartFormDataSection("buildGUID", buildGUID),
                new MultipartFormDataSection("projectId", GetProjectId()),
                new MultipartFormFileSection("file", File.ReadAllBytes(ZipName), Path.GetFileName(ZipName), "application/zip")
            };

            uploadRequest = UnityWebRequest.Post(baseUrl, formSections);
            uploadRequest.SetRequestHeader("Authorization", $"Bearer {access_token}");
            uploadRequest.SetRequestHeader("X-Requested-With", "XMLHTTPREQUEST");

            var op = uploadRequest.SendWebRequest();

            EditorCoroutineUtility.StartCoroutineOwnerless(UpdateProgress(uploadRequest));

            op.completed += operation =>
            {
                if (uploadRequest.isNetworkError || uploadRequest.isHttpError)
                {
                    if (uploadRequest.error != "Request aborted")
                    {
                        Debug.LogError(uploadRequest.error);
                    }
                }
                else
                {
                    var response = JsonUtility.FromJson<UploadResponse>(op.webRequest.downloadHandler.text);
                    if (!string.IsNullOrEmpty(response.key))
                    {
                        QueryProgress(response.key);
                    }
                }
            };
        }

        static void StopUploadAction()
        {
            if (uploadRequest == null) { return; }
            uploadRequest.Abort();
        }

        static void CheckProgress(string key)
        {
            var token = UnityConnectSession.instance.GetAccessToken();
            if (token.Length == 0)
            {
                CheckLoginStatus();
                return;
            }

            key = key ?? GetProjectId();
            string baseUrl = GetAPIBaseUrl();

            var uploadRequest = UnityWebRequest.Get($"{baseUrl + QueryProgressEndpoint}?key={key}");
            uploadRequest.SetRequestHeader("Authorization", $"Bearer {token}");
            uploadRequest.SetRequestHeader("X-Requested-With", "XMLHTTPREQUEST");
            var op = uploadRequest.SendWebRequest();

            op.completed += operation =>
            {
                if (uploadRequest.isNetworkError || uploadRequest.isHttpError)
                {
                    Debug.LogError(uploadRequest.error);
                    StopUploadAction();
                    return;
                }
                var response = JsonUtility.FromJson<GetProgressResponse>(op.webRequest.downloadHandler.text);

                if (response.progress == 100 || !string.IsNullOrEmpty(response.error))
                {
                    SaveProjectID(response.projectId);
                    return;
                }
                EditorCoroutineUtility.StartCoroutineOwnerless(RefreshProcessingProgress(1.5f));
            };
        }

        static void SaveProjectID(string projectId)
        {
            if (projectId.Length == 0) { return; }

            StreamWriter writer = new StreamWriter(WebglSharingFile, false);
            writer.Write(projectId);
            writer.Close();
        }

        static string GetProjectId()
        {
            if (!File.Exists(WebglSharingFile)) { return string.Empty; }

            var reader = new StreamReader(WebglSharingFile);
            var projectId = reader.ReadLine();

            reader.Close();
            return projectId;
        }

        static IEnumerator UpdateProgress(UnityWebRequest request)
        {
            EditorWaitForSeconds waitForSeconds = new EditorWaitForSeconds(0.5f);
            while (true)
            {
                if (request.isDone) { break; }

                int progress = (int)(Mathf.Clamp(request.uploadProgress, 0, 1) * 100);
                Debug.Log($"Upload Progress: {progress}%");
                yield return waitForSeconds;
            }
            yield return null;
        }

        static void CheckLoginStatus()
        {
            var token = UnityConnectSession.instance.GetAccessToken();
            if (token.Length != 0)
            {
                Debug.Log("Connected!");
                return;
            }

            if (waitUntilUserLogsInRoutine != null) { return; }

            waitUntilUserLogsInRoutine = EditorCoroutineUtility.StartCoroutineOwnerless(WaitUntilUserLogsIn(2f));
        }

        static IEnumerator WaitUntilUserLogsIn(float refreshDelay)
        {
            EditorWaitForSeconds waitAmount = new EditorWaitForSeconds(refreshDelay);
            while (EditorWindow.HasOpenInstances<PublisherWindow>())
            {
                yield return waitAmount;
                if (UnityConnectSession.instance.GetAccessToken().Length != 0)
                {
                    Debug.Log("Connected!");
                    waitUntilUserLogsInRoutine = null;
                    yield break;
                }
            }
            waitUntilUserLogsInRoutine = null;
        }

        static IEnumerator RefreshProcessingProgress(float refreshDelay)
        {
            EditorWaitForSeconds waitAmount = new EditorWaitForSeconds(refreshDelay);
            yield return waitAmount;
            QueryProgress(null);
        }

        static string GetAPIBaseUrl()
        {
            // string env = UnityConnectSession.instance.GetEnvironment();
            // if (env == "staging")
            // {
            //     return "https://connect-staging.unity.com";
            // }
            // else if (env == "dev")
            // {
            //     return "https://connect-dev.unity.com";
            // }

            return "https://games.taapgame.com";
        }
    }

    [Serializable]
    public class UploadResponse
    {
        public string key;
    }

    [Serializable]
    public class GetProgressResponse
    {
        public string projectId;
        public string url;
        public int progress;
        public string error;
    }
}
