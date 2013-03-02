//
// Montauk is a tiny minimal web framework for .NET
//
// Updated On: 28 February 2013
// 
// Frank Hale - frankhale@gmail.com
// 			    http://about.me/frank.hale
//
// GPL version 3 <http://www.gnu.org/licenses/gpl-3.0.html>
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <http://www.gnu.org/licenses/>.
//

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using System.Web.SessionState;
using AspNetAdapter;
using Newtonsoft.Json;
using System.Threading;
using MarkdownSharp;
using System.Security.Cryptography;

#region ASSEMBLY INFORMATION
[assembly: AssemblyTitle("Montauk")]
[assembly: AssemblyDescription("A tiny minimal web framework for .NET")]
[assembly: AssemblyCompany("Frank Hale")]
[assembly: AssemblyProduct("Montauk")]
[assembly: AssemblyCopyright("Copyright © 2011-2013")]
[assembly: ComVisible(false)]
[assembly: AssemblyVersion("1.1.0")]
#endregion

namespace Montauk
{
	#region MONTAUK ENGINE
	public class MontaukEngine : IAspNetAdapterApplication
	{
		private static string defaultRoute = "/Index";
		private static string viewRoot = "/Views";
		private static string publicResourcesFolderName = "Resources";
		private static string antiForgeryTokenName = "AntiForgeryToken";
		private static Regex allowedFilePattern = new Regex(@"\.(js|css|png|jpg|gif|ico|pptx|xlsx|csv|txt)$", RegexOptions.Compiled);

		private Dictionary<string, object> app;
		private Dictionary<string, object> request;
		private Action<Dictionary<string, object>> response;
		private Dictionary<string, string> form;
		private List<string> antiForgeryTokens;

		private string appRoot;
		private string path;
		private string requestType;
		private bool debugMode;

		internal ViewEngine viewEngine;

		private List<Type> GetTypeList(Type t)
		{
			List<Type> types = null;

			types = (from assembly in AppDomain.CurrentDomain.GetAssemblies()
							 from type in assembly.GetTypes()
							 where type.BaseType == t
							 select type).ToList();

			return types;
		}

		private Type GetApplication()
		{
			return GetTypeList(typeof(MontaukApplication)).FirstOrDefault();
		}

		public void Init(Dictionary<string, object> app, Dictionary<string, object> request, Action<Dictionary<string, object>> response)
		{
			this.request = request;
			this.app = app;
			this.response = response;

			path = request[HttpAdapterConstants.RequestPath] as string;
			appRoot = request[HttpAdapterConstants.RequestPathBase] as string;
			requestType = request[HttpAdapterConstants.RequestMethod].ToString();
			form = request[HttpAdapterConstants.RequestForm] as Dictionary<string, string>;
			debugMode = Convert.ToBoolean(app[HttpAdapterConstants.DebugMode]);

			viewEngine = GetApplication("__VIEWENGINE__") as ViewEngine;
			antiForgeryTokens = GetApplication("__ANTIFORGERYTOKENS__") as List<string>;

			if (antiForgeryTokens == null)
			{
				antiForgeryTokens = new List<string>();
				AddApplication("__ANTIFORGERYTOKENS__", antiForgeryTokens);
			}

			if (viewEngine == null || debugMode && !allowedFilePattern.IsMatch(path))
			{
				string viewCache = null;
				string cachePath = MapPath("/Views/Cache");
				string cacheFilePath = string.Join("/", cachePath, "viewsCache.json");

				List<IViewCompilerDirectiveHandler> dirHandlers = new List<IViewCompilerDirectiveHandler>();
				List<IViewCompilerSubstitutionHandler> substitutionHandlers = new List<IViewCompilerSubstitutionHandler>();

				dirHandlers.Add(new MasterPageDirective());
				dirHandlers.Add(new PlaceHolderDirective());
				dirHandlers.Add(new PartialPageDirective());
				substitutionHandlers.Add(new CommentSubstitution());
				substitutionHandlers.Add(new AntiForgeryTokenSubstitution(CreateAntiForgeryToken));
				substitutionHandlers.Add(new HeadSubstitution());

				if (!Directory.Exists(cachePath))
				{
					try { Directory.CreateDirectory(cachePath); }
					catch { /* Silently ignore failure */ }
				}
				else if (File.Exists(cacheFilePath) && !debugMode)
					viewCache = File.ReadAllText(cacheFilePath);

				viewEngine = new ViewEngine(appRoot, new string[] { MapPath(viewRoot) }, dirHandlers, substitutionHandlers, viewCache);

				if (string.IsNullOrEmpty(viewCache) || debugMode)
					UpdateCache(cacheFilePath);

				AddApplication("__VIEWENGINE__", viewEngine);
			}

			#region PROCESS REQUEST
			if (path.StartsWith("/" + publicResourcesFolderName))
			{
				string resourcePath = MapPath(path);

				if (allowedFilePattern.IsMatch(resourcePath))
				{
					if (File.Exists(resourcePath))
					{
						string contentType = String.Empty;

						if (path.EndsWith(".css"))
							contentType = "text/css";
						else if (path.EndsWith(".js"))
							contentType = "application/x-javascript";
						else if (path.EndsWith(".jpg"))
							contentType = "image/jpg";
						else if (path.EndsWith(".ico"))
							contentType = "image/x-icon";
						else if (path.EndsWith(".txt"))
							contentType = "text/plain";

						response(new Dictionary<string, object>
						{
							{HttpAdapterConstants.ResponseBody, File.ReadAllBytes(path)},
							{HttpAdapterConstants.ResponseContentType, contentType}
						});
					}
				}
			}
			else
			{
				#region ACTION HANDLER
				if ((path == "/") || (path.ToLower() == "/default.aspx"))
					ResponseRedirect(defaultRoute);

				string route = path;

				Type montaukApp = GetApplication();

				if (montaukApp != null)
				{
					MontaukApplication mApp = (MontaukApplication)Activator.CreateInstance(montaukApp);
					MethodInfo initMethod = montaukApp.GetMethod("Init", BindingFlags.NonPublic | BindingFlags.Instance);
					initMethod.Invoke(mApp, new object[] { this });

					switch (requestType)
					{
						case "GET":
							string getRouteKey = mApp.Get.Keys.OrderByDescending(x => x.Length).FirstOrDefault(x => (route == x) || (route.StartsWith(x)));

							if (!String.IsNullOrEmpty(getRouteKey))
							{
								mApp.CurrentActionRoute = getRouteKey.ReplaceFirstInstance("/", "");
								string[] routeArgs = route.Replace(getRouteKey, String.Empty).Split(new string[] { "/" }, StringSplitOptions.RemoveEmptyEntries);
								string view = mApp.Get[getRouteKey](routeArgs);

								response(new Dictionary<string, object>
								{
									{HttpAdapterConstants.ResponseBody, view},
									{HttpAdapterConstants.ResponseContentType, "text/html"}
								});
								return;
							}
							break;

						case "POST":
							string postRouteKey = mApp.Post.Keys.OrderByDescending(x => x.Length).FirstOrDefault(x => route == x);

							if (!String.IsNullOrEmpty(postRouteKey))
							{
								if (form.ContainsKey(antiForgeryTokenName))
								{
									antiForgeryTokens.Remove(form[antiForgeryTokenName] as string);

									mApp.CurrentActionRoute = postRouteKey.ReplaceFirstInstance("/", "");
									string view = mApp.Post[postRouteKey](form);

									response(new Dictionary<string, object>
									{
										{HttpAdapterConstants.ResponseBody, view},
										{HttpAdapterConstants.ResponseContentType, "text/html"}
									});
									return;
								}
								else
									throw new Exception("All posted forms require an antiforgery token!");
							}
							break;
					};
				}
				#endregion
			}
			#endregion

			response(new Dictionary<string, object>
			{
				{HttpAdapterConstants.ResponseBody, "Http 404 Not Found"},
				{HttpAdapterConstants.ResponseContentType, "text/html"}
			});
		}

		private string MapPath(string path)
		{
			return appRoot + path.Replace('/', '\\');
		}

		private void UpdateCache(string cacheFilePath)
		{
			try
			{
				if (Directory.Exists(Path.GetDirectoryName(cacheFilePath)))
					using (StreamWriter cacheWriter = new StreamWriter(cacheFilePath))
						cacheWriter.Write(viewEngine.GetCache());
			}
			catch { /* Silently ignore any write failures */ }
		}

		internal string CreateAntiForgeryToken()
		{
			string token = (Guid.NewGuid().ToString() + Guid.NewGuid().ToString()).Replace("-", string.Empty);

			antiForgeryTokens.Add(token);

			return token;
		}

		#region ASP.NET ADAPTER CALLBACKS
		public object GetApplication(string key)
		{
			if (app.ContainsKey(HttpAdapterConstants.ApplicationSessionStoreGetCallback) &&
					app[HttpAdapterConstants.ApplicationSessionStoreGetCallback] is Func<string, object>)
				return (app[HttpAdapterConstants.ApplicationSessionStoreGetCallback] as Func<string, object>)(key);

			return null;
		}

		public void AddApplication(string key, object value)
		{
			if (app.ContainsKey(HttpAdapterConstants.ApplicationSessionStoreAddCallback) &&
					app[HttpAdapterConstants.ApplicationSessionStoreAddCallback] is Action<string, object>)
				(app[HttpAdapterConstants.ApplicationSessionStoreAddCallback] as Action<string, object>)(key, value);
		}

		public object GetSession(string key)
		{
			if (app.ContainsKey(HttpAdapterConstants.UserSessionStoreGetCallback) &&
					app[HttpAdapterConstants.UserSessionStoreGetCallback] is Func<string, object>)
				return (app[HttpAdapterConstants.UserSessionStoreGetCallback] as Func<string, object>)(key);

			return null;
		}

		public void AddSession(string key, object value)
		{
			if (app.ContainsKey(HttpAdapterConstants.UserSessionStoreAddCallback) &&
					app[HttpAdapterConstants.UserSessionStoreAddCallback] is Action<string, object>)
				(app[HttpAdapterConstants.UserSessionStoreAddCallback] as Action<string, object>)(key, value);
		}

		public void RemoveSession(string key)
		{
			if (app.ContainsKey(HttpAdapterConstants.UserSessionStoreRemoveCallback) &&
					app[HttpAdapterConstants.UserSessionStoreRemoveCallback] is Action<string>)
				(app[HttpAdapterConstants.UserSessionStoreRemoveCallback] as Action<string>)(key);
		}

		public void ResponseRedirect(string path)
		{
			if (app.ContainsKey(HttpAdapterConstants.ResponseRedirectCallback) &&
					app[HttpAdapterConstants.ResponseRedirectCallback] is Action<string, Dictionary<string, string>>)
				(app[HttpAdapterConstants.ResponseRedirectCallback] as Action<string, Dictionary<string, string>>)(path, null);
		}

		public string GetValidatedFormValue(string key)
		{
			if (request.ContainsKey(HttpAdapterConstants.RequestFormCallback) &&
					request[HttpAdapterConstants.RequestFormCallback] is Func<string, bool, string>)
				return (request[HttpAdapterConstants.RequestFormCallback] as Func<string, bool, string>)(key, true);

			return null;
		}

		public string GetQueryString(string key, bool validated)
		{
			string result = null;

			if (!validated)
			{
				if (request.ContainsKey(HttpAdapterConstants.RequestQueryString) &&
						request[HttpAdapterConstants.RequestQueryString] is Dictionary<string, string>)
					(request[HttpAdapterConstants.RequestQueryString] as Dictionary<string, string>).TryGetValue(key, out result);
			}
			else
			{
				if (request.ContainsKey(HttpAdapterConstants.RequestQueryStringCallback) &&
					request[HttpAdapterConstants.RequestQueryStringCallback] is Func<string, bool, string>)
					result = (request[HttpAdapterConstants.RequestQueryStringCallback] as Func<string, bool, string>)(key, true);
			}

			return result;
		}
		#endregion
	}
	#endregion

	#region MONTAUK APPLICATION
	public abstract class MontaukApplication
	{
		private ViewEngine viewEngine;

		public string CurrentActionRoute { get; internal set; }
		public Dictionary<string, string> ViewTags { get; internal set; }
		public Dictionary<string, Func<string[], string>> Get = new Dictionary<string, Func<string[], string>>();
		public Dictionary<string, Func<Dictionary<string, string>, string>> Post = new Dictionary<string, Func<Dictionary<string, string>, string>>();

		public MontaukApplication()
		{
			ViewTags = new Dictionary<string, string>();
		}

		protected void Init(MontaukEngine engine)
		{
			viewEngine = engine.viewEngine;
		}

		public string View()
		{
			return viewEngine.LoadView(string.Format("Views/App/{0}", CurrentActionRoute), ViewTags);
		}

		public string View(string route)
		{
			return viewEngine.LoadView(route, ViewTags);
		}
	}
	#endregion

	#region VIEW ENGINE
	internal interface IViewCompiler
	{
		List<TemplateInfo> CompileAll();
		TemplateInfo Compile(string fullName);
		TemplateInfo Render(string fullName, Dictionary<string, string> tags);
	}

	internal enum DirectiveProcessType { Compile, AfterCompile, Render }

	internal class TemplateInfo
	{
		public string Name { get; set; }
		public string FullName { get; set; }
		public string Path { get; set; }
		public string Template { get; set; }
		public string TemplateMD5sum { get; set; }
		public string Result { get; set; }
	}

	internal class TemplateLoader
	{
		private string appRoot;
		private string[] viewRoots;

		public TemplateLoader(string appRoot,
													string[] viewRoots)
		{
			appRoot.ThrowIfArgumentNull();

			this.appRoot = appRoot;
			this.viewRoots = viewRoots;
		}

		public List<TemplateInfo> Load()
		{
			List<TemplateInfo> templates = new List<TemplateInfo>();

			foreach (string viewRoot in viewRoots)
			{
				string path = Path.Combine(appRoot, viewRoot);

				if (Directory.Exists(path))
					foreach (FileInfo fi in GetAllFiles(new DirectoryInfo(path), "*.html"))
						templates.Add(Load(fi.FullName));
			}

			return templates;
		}

		// This code was adapted to work with FileInfo/DirectoryInfo but was originally from the following question on SO:
		// http://stackoverflow.com/questions/929276/how-to-recursively-list-all-the-files-in-a-directory-in-c
		public static IEnumerable<FileInfo> GetAllFiles(DirectoryInfo dirInfo, string searchPattern = "")
		{
			Queue<string> queue = new Queue<string>();
			queue.Enqueue(dirInfo.FullName);

			while (queue.Count > 0)
			{
				string path = queue.Dequeue();

				foreach (string subDir in Directory.GetDirectories(path))
					queue.Enqueue(subDir);

				FileInfo[] fileInfos = new DirectoryInfo(path).GetFiles(searchPattern);

				if (fileInfos != null)
					for (int i = 0; i < fileInfos.Length; i++)
						yield return fileInfos[i];
			}
		}

		public TemplateInfo Load(string path)
		{
			string viewRoot = viewRoots.FirstOrDefault(x => path.StartsWith(Path.Combine(appRoot, x)));

			if (string.IsNullOrEmpty(viewRoot)) return null;

			DirectoryInfo rootDir = new DirectoryInfo(viewRoot);

			string extension = Path.GetExtension(path);
			string templateName = Path.GetFileNameWithoutExtension(path);
			string templateKeyName = path.Replace(rootDir.Parent.FullName, string.Empty)
																	 .Replace(appRoot, string.Empty)
																	 .Replace(extension, string.Empty)
																	 .Replace("\\", "/").TrimStart('/');
			string template = File.ReadAllText(path);

			return new TemplateInfo()
			{
				TemplateMD5sum = template.CalculateMD5sum(),
				FullName = templateKeyName,
				Name = templateName,
				Path = path,
				Template = template
			};
		}
	}

	#region DIRECTIVES AND SUBSTITUTIONS
	internal interface IViewCompilerDirectiveHandler
	{
		DirectiveProcessType Type { get; }
		StringBuilder Process(ViewCompilerDirectiveInfo directiveInfo);
	}

	internal interface IViewCompilerSubstitutionHandler
	{
		DirectiveProcessType Type { get; }
		StringBuilder Process(StringBuilder content);
	}

	internal class HeadSubstitution : IViewCompilerSubstitutionHandler
	{
		private static Regex headBlockRE = new Regex(@"\[\[(?<block>[\s\S]+?)\]\]", RegexOptions.Compiled);
		private static string headDirective = "%%Head%%";

		public DirectiveProcessType Type { get; private set; }

		public HeadSubstitution()
		{
			Type = DirectiveProcessType.Compile;
		}

		public StringBuilder Process(StringBuilder content)
		{
			MatchCollection heads = headBlockRE.Matches(content.ToString());

			if (heads.Count > 0)
			{
				StringBuilder headSubstitutions = new StringBuilder();

				foreach (Match head in heads)
				{
					headSubstitutions.Append(Regex.Replace(head.Groups["block"].Value, @"^(\s+)", string.Empty, RegexOptions.Multiline));
					content.Replace(head.Value, string.Empty);
				}

				content.Replace(headDirective, headSubstitutions.ToString());
			}

			content.Replace(headDirective, string.Empty);

			return content;
		}
	}

	internal class AntiForgeryTokenSubstitution : IViewCompilerSubstitutionHandler
	{
		private static string tokenName = "%%AntiForgeryToken%%";
		private Func<string> createAntiForgeryToken;

		public DirectiveProcessType Type { get; private set; }

		public AntiForgeryTokenSubstitution(Func<string> createAntiForgeryToken)
		{
			this.createAntiForgeryToken = createAntiForgeryToken;

			Type = DirectiveProcessType.Render;
		}

		public StringBuilder Process(StringBuilder content)
		{
			var tokens = Regex.Matches(content.ToString(), tokenName)
												.Cast<Match>()
												.Select(m => new { Start = m.Index, End = m.Length })
												.Reverse();

			foreach (var t in tokens)
				content.Replace(tokenName, createAntiForgeryToken(), t.Start, t.End);

			return content;
		}
	}

	internal class CommentSubstitution : IViewCompilerSubstitutionHandler
	{
		private static Regex commentBlockRE = new Regex(@"\@\@(?<block>[\s\S]+?)\@\@", RegexOptions.Compiled);

		public DirectiveProcessType Type { get; private set; }

		public CommentSubstitution()
		{
			Type = DirectiveProcessType.Compile;
		}

		public StringBuilder Process(StringBuilder content)
		{
			return new StringBuilder(commentBlockRE.Replace(content.ToString(), string.Empty));
		}
	}

	internal class MasterPageDirective : IViewCompilerDirectiveHandler
	{
		private static string tokenName = "%%View%%";
		public DirectiveProcessType Type { get; private set; }

		public MasterPageDirective()
		{
			Type = DirectiveProcessType.Compile;
		}

		public StringBuilder Process(ViewCompilerDirectiveInfo directiveInfo)
		{
			if (directiveInfo.Directive == "Master")
			{
				StringBuilder finalPage = new StringBuilder();

				string masterPageName = directiveInfo.DetermineKeyName(directiveInfo.Value);
				string masterPageTemplate = directiveInfo.ViewTemplates.FirstOrDefault(x => x.FullName == masterPageName).Template;

				directiveInfo.AddPageDependency(masterPageName);

				finalPage.Append(masterPageTemplate);
				finalPage.Replace(tokenName, directiveInfo.Content.ToString());
				finalPage.Replace(directiveInfo.Match.Groups[0].Value, string.Empty);

				return finalPage;
			}

			return directiveInfo.Content;
		}
	}

	internal class PartialPageDirective : IViewCompilerDirectiveHandler
	{
		public DirectiveProcessType Type { get; private set; }

		public PartialPageDirective()
		{
			Type = DirectiveProcessType.AfterCompile;
		}

		public StringBuilder Process(ViewCompilerDirectiveInfo directiveInfo)
		{
			if (directiveInfo.Directive == "Partial")
			{
				string partialPageName = directiveInfo.DetermineKeyName(directiveInfo.Value);
				string partialPageTemplate = directiveInfo.ViewTemplates.FirstOrDefault(x => x.FullName == partialPageName).Template;

				directiveInfo.Content.Replace(directiveInfo.Match.Groups[0].Value, partialPageTemplate);
			}

			return directiveInfo.Content;
		}
	}

	internal class BundleDirective : IViewCompilerDirectiveHandler
	{
		private bool debugMode;
		private string sharedResourceFolderPath;
		private Func<string, string[]> getBundleFiles;
		private Dictionary<string, string> bundleLinkResults;
		private static string cssIncludeTag = "<link href=\"{0}\" rel=\"stylesheet\" type=\"text/css\" />";
		private static string jsIncludeTag = "<script src=\"{0}\" type=\"text/javascript\"></script>";

		public DirectiveProcessType Type { get; private set; }

		public BundleDirective(bool debugMode, string sharedResourceFolderPath, Func<string, string[]> getBundleFiles)
		{
			this.debugMode = debugMode;
			this.sharedResourceFolderPath = sharedResourceFolderPath;
			this.getBundleFiles = getBundleFiles;

			bundleLinkResults = new Dictionary<string, string>();

			Type = DirectiveProcessType.AfterCompile;
		}

		public string ProcessBundleLink(string bundlePath)
		{
			string tag = string.Empty;
			string extension = Path.GetExtension(bundlePath).Substring(1).ToLower();
			bool isAPath = bundlePath.Contains('/') ? true : false;
			string modifiedBundlePath = bundlePath;

			if (!isAPath)
				modifiedBundlePath = string.Join("/", sharedResourceFolderPath, extension, bundlePath);

			if (extension == "css")
				tag = string.Format(cssIncludeTag, modifiedBundlePath);
			else if (extension == "js")
				tag = string.Format(jsIncludeTag, modifiedBundlePath);

			return tag;
		}

		public StringBuilder Process(ViewCompilerDirectiveInfo directiveInfo)
		{
			if (directiveInfo.Directive == "Bundle")
			{
				StringBuilder fileLinkBuilder = new StringBuilder();

				string bundleName = directiveInfo.Value;

				if (bundleLinkResults.ContainsKey(bundleName))
					fileLinkBuilder.AppendLine(bundleLinkResults[bundleName]);
				else
				{
					if (!string.IsNullOrEmpty(bundleName))
					{
						if (debugMode)
						{
							var bundles = getBundleFiles(bundleName);

							if (bundles != null)
							{
								foreach (string bundlePath in getBundleFiles(bundleName))
									fileLinkBuilder.AppendLine(ProcessBundleLink(bundlePath));
							}
						}
						else
							fileLinkBuilder.AppendLine(ProcessBundleLink(bundleName));
					}

					bundleLinkResults[bundleName] = fileLinkBuilder.ToString();
				}

				directiveInfo.Content.Replace(directiveInfo.Match.Groups[0].Value, fileLinkBuilder.ToString());
			}

			return directiveInfo.Content;
		}
	}

	internal class PlaceHolderDirective : IViewCompilerDirectiveHandler
	{
		public DirectiveProcessType Type { get; private set; }

		public PlaceHolderDirective()
		{
			Type = DirectiveProcessType.AfterCompile;
		}

		public StringBuilder Process(ViewCompilerDirectiveInfo directiveInfo)
		{
			if (directiveInfo.Directive == "Placeholder")
			{
				Match placeholderMatch = (new Regex(string.Format(@"\[{0}\](?<block>[\s\S]+?)\[/{0}\]", directiveInfo.Value)))
																 .Match(directiveInfo.Content.ToString());

				if (placeholderMatch.Success)
				{
					directiveInfo.Content.Replace(directiveInfo.Match.Groups[0].Value, placeholderMatch.Groups["block"].Value);
					directiveInfo.Content.Replace(placeholderMatch.Groups[0].Value, string.Empty);
				}
			}

			return directiveInfo.Content;
		}
	}
	#endregion

	internal class ViewCompilerDirectiveInfo
	{
		public Match Match { get; set; }
		public string Directive { get; set; }
		public string Value { get; set; }
		public StringBuilder Content { get; set; }
		public List<TemplateInfo> ViewTemplates { get; set; }
		public Func<string, string> DetermineKeyName { get; set; }
		public Action<string> AddPageDependency { get; set; }
	}

	internal class ViewCompiler : IViewCompiler
	{
		private List<IViewCompilerDirectiveHandler> directiveHandlers;
		private List<IViewCompilerSubstitutionHandler> substitutionHandlers;

		private List<TemplateInfo> viewTemplates;
		private List<TemplateInfo> compiledViews;
		private Dictionary<string, List<string>> viewDependencies;
		private Dictionary<string, HashSet<string>> templateKeyNames;

		private static Regex directiveTokenRE = new Regex(@"(\%\%(?<directive>[a-zA-Z0-9]+)=(?<value>(\S|\.)+)\%\%)", RegexOptions.Compiled);
		private static Regex tagRE = new Regex(@"{({|\||\!)([\w]+)(}|\!|\|)}", RegexOptions.Compiled);
		private static Regex emptyLines = new Regex(@"^\s+$[\r\n]*", RegexOptions.Compiled | RegexOptions.Multiline);
		private static string tagFormatPattern = @"({{({{|\||\!){0}(\||\!|}})}})";
		private static string tagEncodingHint = "{|";
		private static string markdownEncodingHint = "{!";
		private static string unencodedTagHint = "{{";

		private StringBuilder directive = new StringBuilder();
		private StringBuilder value = new StringBuilder();

		public ViewCompiler(List<TemplateInfo> viewTemplates,
												List<TemplateInfo> compiledViews,
												Dictionary<string, List<string>> viewDependencies,
												List<IViewCompilerDirectiveHandler> directiveHandlers,
												List<IViewCompilerSubstitutionHandler> substitutionHandlers)
		{
			viewTemplates.ThrowIfArgumentNull();
			compiledViews.ThrowIfArgumentNull();
			viewDependencies.ThrowIfArgumentNull();
			directiveHandlers.ThrowIfArgumentNull();
			substitutionHandlers.ThrowIfArgumentNull();

			this.viewTemplates = viewTemplates;
			this.compiledViews = compiledViews;
			this.viewDependencies = viewDependencies;
			this.directiveHandlers = directiveHandlers;
			this.substitutionHandlers = substitutionHandlers;

			templateKeyNames = new Dictionary<string, HashSet<string>>();
		}

		public List<TemplateInfo> CompileAll()
		{
			foreach (TemplateInfo vt in viewTemplates)
			{
				if (!vt.FullName.Contains("Fragment"))
					Compile(vt.FullName);
				else
				{
					compiledViews.Add(new TemplateInfo()
					{
						FullName = vt.FullName,
						Name = vt.Name,
						Template = vt.Template,
						Result = string.Empty,
						TemplateMD5sum = vt.TemplateMD5sum,
						Path = vt.Path
					});
				}
			}

			return compiledViews;
		}

		public TemplateInfo Compile(string fullName)
		{
			TemplateInfo viewTemplate = viewTemplates.FirstOrDefault(x => x.FullName == fullName);

			if (viewTemplate != null)
			{
				StringBuilder rawView = new StringBuilder(viewTemplate.Template);
				StringBuilder compiledView = new StringBuilder();

				if (!viewTemplate.FullName.Contains("Fragment"))
					compiledView = ProcessDirectives(fullName, rawView);

				if (string.IsNullOrEmpty(compiledView.ToString()))
					compiledView = rawView;

				compiledView.Replace(compiledView.ToString(), Regex.Replace(compiledView.ToString(), @"^\s*$\n", string.Empty, RegexOptions.Multiline));

				TemplateInfo view = new TemplateInfo()
				{
					FullName = fullName,
					Name = viewTemplate.Name,
					Template = compiledView.ToString(),
					Result = string.Empty,
					TemplateMD5sum = viewTemplate.TemplateMD5sum
				};

				TemplateInfo previouslyCompiled = compiledViews.FirstOrDefault(x => x.FullName == viewTemplate.FullName);

				if (previouslyCompiled != null)
					compiledViews.Remove(previouslyCompiled);

				compiledViews.Add(view);

				return view;
			}

			throw new FileNotFoundException(string.Format("Cannot find view : {0}", fullName));
		}

		public TemplateInfo Render(string fullName, Dictionary<string, string> tags)
		{
			TemplateInfo compiledView = compiledViews.FirstOrDefault(x => x.FullName == fullName);

			if (compiledView != null)
			{
				StringBuilder compiledViewSB = new StringBuilder(compiledView.Template);

				foreach (IViewCompilerSubstitutionHandler sub in substitutionHandlers.Where(x => x.Type == DirectiveProcessType.Render))
					compiledViewSB = sub.Process(compiledViewSB);

				if (tags != null)
				{
					StringBuilder tagSB = new StringBuilder();

					foreach (Match match in emptyLines.Matches(compiledViewSB.ToString()))
						compiledViewSB.Replace(match.Value, string.Empty);

					foreach (KeyValuePair<string, string> tag in tags)
					{
						tagSB.Clear();
						tagSB.Insert(0, string.Format(tagFormatPattern, tag.Key));

						Regex tempTagRE = new Regex(tagSB.ToString());

						MatchCollection tagMatches = tempTagRE.Matches(compiledViewSB.ToString());

						if (tagMatches != null)
						{
							foreach (Match m in tagMatches)
							{
								if (!string.IsNullOrEmpty(tag.Value))
								{
									if (m.Value.StartsWith(unencodedTagHint))
										compiledViewSB.Replace(m.Value, tag.Value.Trim());
									else if (m.Value.StartsWith(tagEncodingHint))
										compiledViewSB.Replace(m.Value, HttpUtility.HtmlEncode(tag.Value.Trim()));
									else if (m.Value.StartsWith(markdownEncodingHint))
										compiledViewSB.Replace(m.Value, new Markdown().Transform((tag.Value.Trim())));
								}
							}
						}
					}

					MatchCollection leftoverMatches = tagRE.Matches(compiledViewSB.ToString());

					if (leftoverMatches != null)
						foreach (Match match in leftoverMatches)
							compiledViewSB.Replace(match.Value, string.Empty);
				}

				compiledView.Result = compiledViewSB.ToString();

				return compiledView;
			}

			return null;
		}

		private StringBuilder ProcessDirectives(string fullViewName, StringBuilder rawView)
		{
			StringBuilder pageContent = new StringBuilder(rawView.ToString());

			if (!viewDependencies.ContainsKey(fullViewName))
				viewDependencies[fullViewName] = new List<string>();

			Func<string, string> determineKeyName = name =>
			{
				return viewTemplates.Select(y => y.FullName)
														.Where(z => z.Contains("Shared/" + name))
														.FirstOrDefault();
			};

			Action<string> addPageDependency = x =>
			{
				if (!viewDependencies[fullViewName].Contains(x))
					viewDependencies[fullViewName].Add(x);
			};

			Action<IEnumerable<IViewCompilerDirectiveHandler>> performCompilerPass = x =>
			{
				MatchCollection dirMatches = directiveTokenRE.Matches(pageContent.ToString());

				foreach (Match match in dirMatches)
				{
					directive.Clear();
					directive.Insert(0, match.Groups["directive"].Value);

					value.Clear();
					value.Insert(0, match.Groups["value"].Value);

					foreach (IViewCompilerDirectiveHandler handler in x)
					{
						pageContent.Replace(pageContent.ToString(),
								handler.Process(new ViewCompilerDirectiveInfo()
								{
									Match = match,
									Directive = directive.ToString(),
									Value = value.ToString(),
									Content = pageContent,
									ViewTemplates = viewTemplates,
									DetermineKeyName = determineKeyName,
									AddPageDependency = addPageDependency
								}).ToString());
					}
				}
			};

			performCompilerPass(directiveHandlers.Where(x => x.Type == DirectiveProcessType.Compile));

			foreach (IViewCompilerSubstitutionHandler sub in substitutionHandlers.Where(x => x.Type == DirectiveProcessType.Compile))
				pageContent = sub.Process(pageContent);

			performCompilerPass(directiveHandlers.Where(x => x.Type == DirectiveProcessType.AfterCompile));

			return pageContent;
		}

		public void RecompileDependencies(string fullViewName)
		{
			var deps = viewDependencies.Where(x => x.Value.FirstOrDefault(y => y == fullViewName) != null);

			Action<string> compile = name =>
			{
				var template = viewTemplates.FirstOrDefault(x => x.FullName == name);

				if (template != null)
					Compile(template.FullName);
			};

			if (deps.Count() > 0)
			{
				foreach (KeyValuePair<string, List<string>> view in deps)
				{
					compile(view.Key);
				}
			}
			else
				compile(fullViewName);
		}
	}

	public interface IViewEngine
	{
		string LoadView(string fullName, Dictionary<string, string> tags);
		string GetCache();
		bool CacheUpdated { get; }
	}

	internal class ViewCache
	{
		public List<TemplateInfo> ViewTemplates;
		public List<TemplateInfo> CompiledViews;
		public Dictionary<string, List<string>> ViewDependencies;
	}

	internal class ViewEngine : IViewEngine
	{
		private string appRoot;
		private List<IViewCompilerDirectiveHandler> dirHandlers;
		private List<IViewCompilerSubstitutionHandler> substitutionHandlers;
		private List<TemplateInfo> viewTemplates;
		private List<TemplateInfo> compiledViews;
		private Dictionary<string, List<string>> viewDependencies;
		private TemplateLoader viewTemplateLoader;
		private ViewCompiler viewCompiler;

		public bool CacheUpdated { get; private set; }

		public ViewEngine(string appRoot,
											string[] viewRoots,
											List<IViewCompilerDirectiveHandler> dirHandlers,
											List<IViewCompilerSubstitutionHandler> substitutionHandlers,
											string cache)
		{
			this.appRoot = appRoot;

			this.dirHandlers = dirHandlers;
			this.substitutionHandlers = substitutionHandlers;

			viewTemplateLoader = new TemplateLoader(appRoot, viewRoots);

			FileSystemWatcher watcher = new FileSystemWatcher(appRoot, "*.html");

			watcher.NotifyFilter = NotifyFilters.LastWrite;
			watcher.Changed += new FileSystemEventHandler(OnChanged);
			watcher.IncludeSubdirectories = true;
			watcher.EnableRaisingEvents = true;

			viewTemplates = new List<TemplateInfo>();
			compiledViews = new List<TemplateInfo>();
			viewDependencies = new Dictionary<string, List<string>>();

			if (!(viewRoots.Count() >= 1))
				throw new ArgumentException("At least one view root is required to load view templates from.");

			ViewCache viewCache = null;

			if (!string.IsNullOrEmpty(cache))
			{
				viewCache = JsonConvert.DeserializeObject<ViewCache>(cache);

				if (viewCache != null)
				{
					viewTemplates = viewCache.ViewTemplates;
					compiledViews = viewCache.CompiledViews;
					viewDependencies = viewCache.ViewDependencies;
				}
			}

			if (viewCache == null)
			{
				viewTemplates = viewTemplateLoader.Load();

				if (!(viewTemplates.Count() > 0))
					throw new Exception("Failed to load any view templates.");
			}

			viewCompiler = new ViewCompiler(viewTemplates, compiledViews, viewDependencies, dirHandlers, substitutionHandlers);

			if (!(compiledViews.Count() > 0))
				compiledViews = viewCompiler.CompileAll();
		}

		private void OnChanged(object sender, FileSystemEventArgs e)
		{
			FileSystemWatcher fsw = sender as FileSystemWatcher;

			try
			{
				fsw.EnableRaisingEvents = false;

				while (CanOpenForRead(e.FullPath) == false)
					Thread.Sleep(1000);

				var changedTemplate = viewTemplateLoader.Load(e.FullPath);
				viewTemplates.Remove(viewTemplates.Find(x => x.FullName == changedTemplate.FullName));
				viewTemplates.Add(changedTemplate);

				var cv = compiledViews.FirstOrDefault(x => x.FullName == changedTemplate.FullName && x.TemplateMD5sum != changedTemplate.TemplateMD5sum);

				if (cv != null && !changedTemplate.FullName.Contains("Fragment"))
				{
					cv.TemplateMD5sum = changedTemplate.TemplateMD5sum;
					cv.Template = changedTemplate.Template;
					cv.Result = string.Empty;
				}

				viewCompiler = new ViewCompiler(viewTemplates, compiledViews, viewDependencies, dirHandlers, substitutionHandlers);

				if (cv != null)
					viewCompiler.RecompileDependencies(changedTemplate.FullName);
				else
					viewCompiler.Compile(changedTemplate.FullName);

				CacheUpdated = true;
			}
			finally
			{
				fsw.EnableRaisingEvents = true;
			}
		}

		public string GetCache()
		{
			if (CacheUpdated) CacheUpdated = false;

			return JsonConvert.SerializeObject(new ViewCache()
			{
				CompiledViews = compiledViews,
				ViewTemplates = viewTemplates,
				ViewDependencies = viewDependencies
			}, Formatting.Indented);
		}

		// adapted from: http://stackoverflow.com/a/8218033/170217
		private static bool CanOpenForRead(string filePath)
		{
			try
			{
				using (FileStream file = new FileStream(filePath, FileMode.Open, FileAccess.Read))
				{
					file.Close();
					return true;
				}
			}
			catch
			{
				return false;
			}
		}

		public string LoadView(string fullName, Dictionary<string, string> tags)
		{
			string result = null;

			var renderedView = viewCompiler.Render(fullName, tags);

			if (renderedView != null)
				result = renderedView.Result;

			return result;
		}
	}
	#endregion

	#region EXTENSION METHODS
	public static class Extensions
	{
		// Grabbed this extension method from an answer to this question:
		//
		// http://stackoverflow.com/questions/141045/how-do-i-replace-the-first-instance-of-a-string-in-net
		public static string ReplaceFirstInstance(this string source, string find, string replace)
		{
			int index = source.IndexOf(find);
			return index < 0 ? source : source.Substring(0, index) + replace +
					 source.Substring(index + find.Length);
		}

		public static void ThrowIfArgumentNull<T>(this T t, string message = null)
		{
			string argName = t.GetType().Name;

			if (t == null)
				throw new ArgumentNullException(argName, message);
			else if ((t is string) && (t as string) == string.Empty)
				throw new ArgumentException(argName, message);
		}

		// from: http://blogs.msdn.com/b/csharpfaq/archive/2006/10/09/how-do-i-calculate-a-md5-hash-from-a-string_3f00_.aspx
		public static string CalculateMD5sum(this string input)
		{
			MD5 md5 = System.Security.Cryptography.MD5.Create();
			byte[] inputBytes = System.Text.Encoding.UTF8.GetBytes(input);
			byte[] hash = md5.ComputeHash(inputBytes);

			StringBuilder sb = new StringBuilder();

			for (int i = 0; i < hash.Length; i++)
				sb.Append(hash[i].ToString("X2"));

			return sb.ToString();
		}
	}
	#endregion
}