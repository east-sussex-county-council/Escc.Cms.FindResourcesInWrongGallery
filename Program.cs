using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Configuration;
using System.Linq;
using System.Net.Mail;
using System.Text;
using System.Text.RegularExpressions;
using EsccWebTeam.Cms;
using EsccWebTeam.Cms.Permissions;
using Microsoft.ApplicationBlocks.ExceptionManagement;
using Microsoft.ContentManagement.Publishing;
using Microsoft.ContentManagement.Publishing.Extensions.Placeholders;

namespace Escc.Cms.FindResourcesInWrongGallery
{
    /// <summary>
    /// When rearranging content in Resource Manager for web authors, it needs to be in the right gallery or they will not have access to save the page.
    /// This tool checks to ensure resources are in the right gallery.
    /// </summary>
    /// <remarks>Use the <code>-reportconflicts</code> switch to include resources which belong in more than one gallery</remarks>
    class Program
    {
        private static Dictionary<string, ResourceLocation> resourcesToMove = new Dictionary<string, ResourceLocation>();
        private static readonly Dictionary<string, ResourceLocation> resourcesUsed = new Dictionary<string, ResourceLocation>();
        private static readonly NameValueCollection ignoreChannels = ConfigurationManager.GetSection("EsccWebTeam.Cms/IgnoreChannels") as NameValueCollection;
        private static bool reportConflicts;

        static void Main(string[] args)
        {
            try
            {
                reportConflicts = (args.Length == 1 && args[0].Length > 0 && args[0].ToUpperInvariant().Substring(1) == "REPORTCONFLICTS");

                var traverser = new CmsTraverser();
                traverser.TraversingPlaceholder += new CmsEventHandler(traverser_TraversingPlaceholder);
                traverser.TraverseSite(PublishingMode.Unpublished, false);

                SortResourcesToMove();

                var body = BuildEmailHtml();

                if (!String.IsNullOrEmpty(body))
                {
                    SendEmail(body);
                }
            }
            catch (Exception ex)
            {
                ExceptionManager.Publish(ex);
                throw;
            }
        }

        /// <summary>
        /// Sorts the resources to move collection, because it's easier to work through the report in order
        /// </summary>
        private static void SortResourcesToMove()
        {
            var sortedKeys = new List<string>(resourcesToMove.Keys);
            sortedKeys.Sort();

            var sortedResources = sortedKeys.ToDictionary(key => key, key => resourcesToMove[key]);
            resourcesToMove = sortedResources;
        }

        private static void SendEmail(string body)
        {
            using (var mail = new MailMessage(ConfigurationManager.AppSettings["EmailFrom"], ConfigurationManager.AppSettings["EmailTo"]))
            {
                mail.IsBodyHtml = true;
                mail.Subject = "CMS resources to move";
                mail.Body = body;

                var smtp = new SmtpClient();
                smtp.Send(mail);
            }
        }

        private static string BuildEmailHtml()
        {
            var body = new StringBuilder();
            foreach (ResourceLocation resource in resourcesToMove.Values)
            {
                if (ShouldReportResource(resource))
                {
                    if (body.Length == 0)
                    {
                        body.Append("<html><body style=\"font-family: Arial\"><ol>");
                    }

                    body.Append("<li><b>").Append(resource.CurrentPath).Append("</b><br />Belongs in:<ul>");
                    foreach (string folder in resource.BelongsInFolder)
                    {
                        body.Append("<li>").Append(folder).Append("</li>");
                    }

                    if (resourcesUsed.ContainsKey(resource.Guid))
                    {
                        foreach (string cmsGroup in resourcesUsed[resource.Guid].BelongsInFolder)
                        {
                            body.Append("<li>").Append(cmsGroup).Append("</li>");
                        }
                    }

                    body.Append("</ul></li>");
                }
            }
            if (body.Length > 0) body.Append("</ol></body></html>");
            return body.ToString();
        }

        /// <summary>
        /// Should we include this resource in the report? Depends on -reportconflicts option.
        /// </summary>
        /// <param name="resource"></param>
        /// <returns></returns>
        private static bool ShouldReportResource(ResourceLocation resource)
        {
            int requiredGalleryCount = resource.BelongsInFolder.Count;
            if (resourcesUsed.ContainsKey(resource.Guid))
            {
                requiredGalleryCount += resourcesUsed[resource.Guid].BelongsInFolder.Count;
            }
            return (requiredGalleryCount == 1 || reportConflicts);
        }

        private static void traverser_TraversingPlaceholder(object sender, CmsEventArgs e)
        {
            if (ignoreChannels != null && !String.IsNullOrEmpty(ignoreChannels[e.Channel.Guid]))
            {
                return;
            }

            // Ignore expired postings
            if (e.Posting.ExpiryDate <= DateTime.Now) return;

            Console.WriteLine(e.Posting.UrlModePublished + ": " + e.Placeholder.Name);

            var image = e.Placeholder as ImagePlaceholder;
            if (image != null)
            {
                var resource = CmsUtilities.ParseResourceUrl(image.Src, e.Context);
                var cmsGroups = CmsPermissions.ReadCmsGroupsForChannel(e.Channel);
                if (cmsGroups[CmsRole.Editor].Count == 0) return;

                IsResourceInTheRightFolder(e, resource, cmsGroups);
            }
            else
            {
                var resourceLinks = Regex.Matches(e.Placeholder.Datasource.RawContent, CmsUtilities.DownloadLinkPattern, RegexOptions.IgnoreCase);
                if (resourceLinks.Count == 0) return;

                var cmsGroups = CmsPermissions.ReadCmsGroupsForChannel(e.Channel);
                if (cmsGroups[CmsRole.Editor].Count == 0) return;

                foreach (Match match in resourceLinks)
                {
                    var resource = CmsUtilities.ParseResourceUrl(match.Groups["url"].Value, e.Context);
                    IsResourceInTheRightFolder(e, resource, cmsGroups);
                }
            }
        }

        private static bool IsResourceInTheRightFolder(CmsEventArgs e, Resource resource, Dictionary<CmsRole, IList<string>> cmsGroups)
        {
            if (resource != null && resource.Parent != null && resource.Parent.Parent != null)
            {
                // Get the resource gallery which should match the name of the CMS group
                var gallery = resource.Parent;
                while (gallery.Parent != null && gallery.Parent.Parent != null && gallery.Parent.Parent != e.Context.RootResourceGallery)
                {
                    gallery = gallery.Parent;
                }

                // Is the resource in a folder named after the group?
                foreach (var cmsGroup in cmsGroups[CmsRole.Editor])
                {
                    if (cmsGroup.ToUpperInvariant() == gallery.Name.ToUpperInvariant())
                    {
                        // It's a match, so no problem here. But save details in case resource used across two groups.
                        if (!resourcesUsed.ContainsKey(resource.Guid))
                        {
                            resourcesUsed.Add(resource.Guid, new ResourceLocation());
                        }
                        if (!resourcesUsed[resource.Guid].BelongsInFolder.Contains(cmsGroup))
                        {
                            resourcesUsed[resource.Guid].BelongsInFolder.Add(cmsGroup);
                        }
                        if (!resourcesUsed[resource.Guid].UsedOnPages.Contains(PostingUrl(e.Posting)))
                        {
                            resourcesUsed[resource.Guid].UsedOnPages.Add(PostingUrl(e.Posting));
                        }
                        return true;
                    }
                }

                // Getting here means we have a resource and a channel with a web author, but no matching name
                FoundResourceToMove(PostingUrl(e.Posting), resource, cmsGroups);
                return false;
            }
            return true;
        }

        private static string PostingUrl(Posting posting)
        {
            if (posting.State == PostingState.Published)
            {
                return posting.UrlModePublished;
            }
            else
            {
                return posting.UrlModeUnpublished;
            }
        }

        private static void FoundResourceToMove(string postingUrl, Resource resource, Dictionary<CmsRole, IList<string>> cmsGroups)
        {
            if (!resourcesToMove.ContainsKey(resource.Guid))
            {
                var resourceToMove = new ResourceLocation()
                    {
                        Guid = resource.Guid,
                        CurrentPath = resource.Path
                    };
                resourcesToMove.Add(resource.Guid, resourceToMove);
            }

            if (!resourcesToMove[resource.Guid].BelongsInFolder.Contains(cmsGroups[CmsRole.Editor][0]))
            {
                resourcesToMove[resource.Guid].BelongsInFolder.Add(cmsGroups[CmsRole.Editor][0]);
            }

            if (!resourcesToMove[resource.Guid].UsedOnPages.Contains(postingUrl))
            {
                resourcesToMove[resource.Guid].UsedOnPages.Add(postingUrl);
            }
        }

        private class ResourceLocation
        {
            public ResourceLocation()
            {
                this.BelongsInFolder = new List<string>();
                this.UsedOnPages = new List<string>();
            }

            public string Guid { get; set; }
            public string CurrentPath { get; set; }
            public List<string> BelongsInFolder { get; set; }
            public List<string> UsedOnPages { get; set; }
        }
    }
}
