﻿using SharpGen.Model;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SharpGen.Transform
{
    public static class TransformServiceExtensions
    {
        private static readonly Regex RegexLinkStart = new Regex(@"^\s*\{\{.*?}}\s*(.*)", RegexOptions.Compiled);
        private static readonly Regex RegexLink = new Regex(@"\{\{(.*?)}}", RegexOptions.Compiled);
        private static readonly Regex RegexSpaceBegin = new Regex(@"^\s*(.*)", RegexOptions.Compiled);
        
        public static IEnumerable<string> GetDocItems(this IDocumentationLinker aggregator, CsBase element)
        {
            var docItems = new List<string>();

            var description = element.Description;
            var remarks = element.Remarks;

            description = RegexSpaceBegin.Replace(description, "$1");

            description = RegexLink.Replace(description, aggregator.ReplaceCRefReferences);
            // evaluator => "<see cref=\"$1\"/>"

            docItems.Add("<summary>");
            docItems.AddRange(description.Split('\n'));
            docItems.Add("</summary>");

            element.FillDocItems(docItems, aggregator);

            if (!string.IsNullOrEmpty(remarks))
            {
                remarks = RegexSpaceBegin.Replace(remarks, "$1");
                remarks = RegexLink.Replace(remarks, aggregator.ReplaceCRefReferences);

                docItems.Add("<remarks>");
                docItems.AddRange(remarks.Split('\n'));
                docItems.Add("</remarks>");
            }

            if (element.CppElementName != null)
            {
                if (element.DocId != null)
                {
                    docItems.Add("<msdn-id>" + Utilities.EscapeXml(element.DocId) + "</msdn-id>");
                }
                docItems.Add("<unmanaged>" + Utilities.EscapeXml(element.DocUnmanagedName) + "</unmanaged>");
                docItems.Add("<unmanaged-short>" + Utilities.EscapeXml(element.DocUnmanagedShortName) + "</unmanaged-short>");
            }

            return docItems;
        }
        
        public static string GetSingleDoc(this IDocumentationLinker aggregator, CsBase element)
        {
            var description = element.Description;

            if (RegexLinkStart.Match(description).Success)
                description = RegexLinkStart.Replace(description, "$1");

            description = RegexSpaceBegin.Replace(description, "$1");

            description = RegexLink.Replace(description, aggregator.ReplaceCRefReferences);

            var docItems = new StringBuilder();

            foreach (var line in description.Split('\n'))
            {
                docItems.Append(line);
            }

            return docItems.ToString();
        }


        private static readonly Regex regexWithMethodW = new Regex("([^W])::");
        private static readonly Regex regexWithTypeW = new Regex("([^W])$");

        public static string ReplaceCRefReferences(this IDocumentationLinker linker, Match match)
        {
            var matchName = match.Groups[1].Value;
            var csName = linker.FindDocName(matchName);

            // Tries to match with W::
            if (csName == null && regexWithMethodW.Match(matchName).Success)
                csName = linker.FindDocName(regexWithMethodW.Replace(matchName, "$1W::"));

            // Or with W
            if (csName == null && regexWithTypeW.Match(matchName).Success)
                csName = linker.FindDocName(regexWithTypeW.Replace(matchName, "$1W"));

            if (csName == null)
                return matchName;

            if (csName.StartsWith("<"))
                return csName;
            return string.Format(CultureInfo.InvariantCulture, "<see cref=\"{0}\"/>", csName);
        }
    }
}