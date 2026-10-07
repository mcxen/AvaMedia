using System.IO.Compression;
using System.Security;
using System.Text;
using System.Xml.Linq;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace AvaMedia.Core;
public static class DocumentEngine
{
    private static readonly object FontGate = new();
    public static void Execute(Job job,Action<double> progress,CancellationToken ct)
    {
        var op=Catalog.Find(job.FeatureId).Operation;
        if(op is Operation.PdfMerge or Operation.PdfSplit)
        {
            var options=job.Options.Clone();
            if(options.Pdf is null)
            {
                options.Pdf=new(){SplitEveryPage=op==Operation.PdfSplit};
                for(var index=0;index<job.Inputs.Length;index++)
                {
                    ct.ThrowIfCancellationRequested();using var pdf=PdfReader.Open(job.Inputs[index],PdfDocumentOpenMode.Import);
                    options.Pdf.Pages.AddRange(Enumerable.Range(1,pdf.PageCount).Select(number=>new PdfPageSelection(index,number)));
                }
            }
            PdfTools.Arrange(new Job{FeatureId=job.FeatureId,Inputs=job.Inputs,Output=job.Output,Options=options},progress,ct);progress(100);return;
        }
        if(op==Operation.PdfAge){PdfTools.Rasterize(job,progress,ct);progress(100);return;}
        if(op==Operation.PdfCompress){PdfTools.Compress(job,progress,ct);progress(100);return;}
        if(job.Options.Pdf is not null)
        {
            PdfTools.Validate(job);
            if(op==Operation.TextPdf)
            {
                var temporary=Path.Combine(Path.GetTempPath(),"AvaMedia-text-"+Guid.NewGuid()+".pdf");
                try
                {
                    CreateTextPdf(job.Inputs[0],temporary,job.Options.Pdf,ct);
                    PdfTools.Arrange(new Job{FeatureId=job.FeatureId,Inputs=[temporary],Output=job.Output,Options=job.Options},progress,ct);
                }
                finally{if(File.Exists(temporary))File.Delete(temporary);}
                progress(100);return;
            }
        }
        if(op==Operation.Zip)
        {
            using var zip=ZipFile.Open(job.Output,ZipArchiveMode.Create);var used=new HashSet<string>();
            foreach(var path in job.Inputs){ct.ThrowIfCancellationRequested();var name=Path.GetFileName(path);for(int i=1;!used.Add(name);i++)name=Path.GetFileNameWithoutExtension(path)+$" ({i})"+Path.GetExtension(path);zip.CreateEntryFromFile(path,name);}
        }
        else if(op==Operation.Unzip)
        {
            Directory.CreateDirectory(job.Output);var root=Path.GetFullPath(job.Output)+Path.DirectorySeparatorChar;
            using var zip=ZipFile.OpenRead(job.Inputs[0]);int count=0;
            foreach(var entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();var destination=Path.GetFullPath(Path.Combine(root,entry.FullName));
                if(!destination.StartsWith(root,OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal))throw new InvalidDataException("压缩包包含越界路径。");
                if(string.IsNullOrEmpty(entry.Name))Directory.CreateDirectory(destination);
                else {Directory.CreateDirectory(Path.GetDirectoryName(destination)!);entry.ExtractToFile(destination,false);}progress(++count*100d/Math.Max(1,zip.Entries.Count));
            }
        }
        else if(op==Operation.ImagesPdf)
        {
            using var pdf=new PdfDocument();
            var pages=job.Options.Pdf?.Pages??job.Inputs.Select((_,index)=>new PdfPageSelection(index,1)).ToList();
            foreach(var selection in pages)
            {
                ct.ThrowIfCancellationRequested();using var image=XImage.FromFile(job.Inputs[selection.InputIndex]);var page=pdf.AddPage();
                var layout=job.Options.Pdf;
                if(layout is null)
                {
                    page.Width=XUnit.FromPoint(image.PointWidth);page.Height=XUnit.FromPoint(image.PointHeight);
                    using var graphics=XGraphics.FromPdfPage(page);graphics.DrawImage(image,0,0,page.Width.Point,page.Height.Point);
                }
                else
                {
                    var width=image.PointWidth;var height=image.PointHeight;
                    var rotated=selection.Rotation is 90 or 270;
                    var size=PdfTools.PaperSize(rotated?height:width,rotated?width:height,layout);
                    page.Width=XUnit.FromPoint(size.Width);page.Height=XUnit.FromPoint(size.Height);
                    var scale=Math.Min((size.Width-layout.Margin*2)/(rotated?height:width),(size.Height-layout.Margin*2)/(rotated?width:height));
                    using var graphics=XGraphics.FromPdfPage(page);graphics.TranslateTransform(size.Width/2,size.Height/2);graphics.RotateTransform(selection.Rotation);
                    graphics.DrawImage(image,-width*scale/2,-height*scale/2,width*scale,height*scale);
                }
            }
            PdfTools.SaveNew(pdf,job.Output,ct);
        }
        else if(op==Operation.TextPdf) CreateTextPdf(job.Inputs[0],job.Output,null,ct);
        else
        {
            var pages=new List<string>();
            var selections=job.Options.Pdf?.Pages;
            var sources=new Dictionary<int,UglyToad.PdfPig.PdfDocument>();
            try
            {
                if(selections is null)
                {
                    var pdf=UglyToad.PdfPig.PdfDocument.Open(job.Inputs[0]);sources.Add(0,pdf);
                    selections=Enumerable.Range(1,pdf.NumberOfPages).Select(number=>new PdfPageSelection(0,number)).ToList();
                }
                foreach(var selection in selections)
                {
                    ct.ThrowIfCancellationRequested();
                    if(!sources.TryGetValue(selection.InputIndex,out var pdf))sources.Add(selection.InputIndex,pdf=UglyToad.PdfPig.PdfDocument.Open(job.Inputs[selection.InputIndex]));
                    pages.Add(ContentOrderTextExtractor.GetText(pdf.GetPage(selection.PageNumber)));progress(pages.Count*80d/selections.Count);
                }
            }
            finally{foreach(var pdf in sources.Values)pdf.Dispose();}
            if(op==Operation.PdfText)File.WriteAllText(job.Output,string.Join("\n\f\n",pages),Encoding.UTF8);
            else if(op==Operation.PdfDocx)WriteDocx(job.Output,pages);
            else if(op==Operation.PdfXlsx)WriteXlsx(job.Output,pages);
        }
        progress(100);
    }
    public static void CreateTextPdf(string source,string output,PdfToolOptions? layout,CancellationToken ct)
    {
        layout??=new();
        lock(FontGate)GlobalFontSettings.FontResolver??=new SystemFontResolver();
        using var pdf=new PdfDocument();var font=new XFont("AvaMedia",layout.FontSize);
        var size=PdfTools.PaperSize(595.28,841.89,layout);var margin=Math.Max(12,layout.Margin);
        PdfPage? page=null;XGraphics? graphics=null;double y=0;
        try
        {
            foreach(var line in File.ReadLines(source))
            {
                ct.ThrowIfCancellationRequested();var remaining=line;
                do
                {
                    if(page is null || y>size.Height-margin)
                    {
                        graphics?.Dispose();page=pdf.AddPage();page.Width=XUnit.FromPoint(size.Width);page.Height=XUnit.FromPoint(size.Height);
                        graphics=XGraphics.FromPdfPage(page);y=margin+layout.FontSize;
                    }
                    var count=remaining.Length;
                    while(count>1 && graphics!.MeasureString(remaining[..count],font).Width>size.Width-margin*2)count--;
                    if(count>0 && count<remaining.Length && char.IsHighSurrogate(remaining[count-1]))count--;
                    if(count==0 && remaining.Length>0)count=Math.Min(2,remaining.Length);
                    graphics!.DrawString(remaining[..count],font,XBrushes.Black,new XPoint(margin,y));y+=layout.FontSize*1.5;remaining=remaining[count..];
                }while(remaining.Length>0);
            }
            if(pdf.PageCount==0){page=pdf.AddPage();page.Width=XUnit.FromPoint(size.Width);page.Height=XUnit.FromPoint(size.Height);}
            PdfTools.SaveNew(pdf,output,ct);
        }
        finally{graphics?.Dispose();}
    }
    private static string Escape(string text) => SecurityElement.Escape(text)??"";
    private static void Entry(ZipArchive zip,string name,string xml){using var writer=new StreamWriter(zip.CreateEntry(name).Open(),new UTF8Encoding(false));writer.Write(xml);}
    private static void WriteDocx(string path,IEnumerable<string> pages)
    {
        using var zip=ZipFile.Open(path,ZipArchiveMode.Create);
        Entry(zip,"[Content_Types].xml","<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>");
        Entry(zip,"_rels/.rels","<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>");
        Entry(zip,"word/document.xml","<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>"+string.Join("<w:p><w:r><w:br w:type=\"page\"/></w:r></w:p>",pages.Select(p=>string.Concat(p.Split('\n').Select(l=>"<w:p><w:r><w:t xml:space=\"preserve\">"+Escape(l)+"</w:t></w:r></w:p>"))))+"</w:body></w:document>");
    }
    private static void WriteXlsx(string path,IEnumerable<string> pages)
    {
        using var zip=ZipFile.Open(path,ZipArchiveMode.Create);
        Entry(zip,"[Content_Types].xml","<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/></Types>");
        Entry(zip,"_rels/.rels","<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
        Entry(zip,"xl/workbook.xml","<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"PDF文本\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
        Entry(zip,"xl/_rels/workbook.xml.rels","<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/></Relationships>");
        int row=0;var xml=new StringBuilder("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
        foreach(var p in pages)foreach(var l in p.Split('\n')){row++;xml.Append($"<row r=\"{row}\"><c r=\"A{row}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{Escape(l)}</t></is></c></row>");}
        Entry(zip,"xl/worksheets/sheet1.xml",xml+"</sheetData></worksheet>");
    }
    private sealed class SystemFontResolver : IFontResolver
    {
        public FontResolverInfo ResolveTypeface(string familyName,bool bold,bool italic)=>new("system");
        public byte[] GetFont(string faceName)
        {
            var dir=Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            foreach(var name in new[]{"msyh.ttc","simsun.ttc","arial.ttf","segoeui.ttf"}){var path=Path.Combine(dir,name);if(File.Exists(path))return ExtractFont(File.ReadAllBytes(path));}
            foreach(var path in new[]{"/System/Library/Fonts/Supplemental/Arial Unicode.ttf","/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf","/System/Library/Fonts/Supplemental/Arial.ttf"})if(File.Exists(path))return ExtractFont(File.ReadAllBytes(path));
            throw new FileNotFoundException("未找到可用的系统字体。");
        }
        // PDFsharp consumes a single sfnt font. Windows CJK fonts are often TTC collections.
        private static byte[] ExtractFont(byte[] data)
        {
            uint U32(int p)=>System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p,4));
            if(data.Length<16 || U32(0)!=0x74746366)return data;
            int start=checked((int)U32(12));int count=System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(start+4,2));
            var size=12+16*count;for(int i=0;i<count;i++)size+=((int)U32(start+12+i*16+12)+3)&~3;
            var output=new byte[size];Array.Copy(data,start,output,0,12+16*count);int cursor=12+16*count;
            for(int i=0;i<count;i++)
            {
                int record=start+12+i*16,offset=(int)U32(record+8),length=(int)U32(record+12);
                Array.Copy(data,offset,output,cursor,length);System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(12+i*16+8,4),(uint)cursor);cursor+=(length+3)&~3;
            }
            return output;
        }
    }
}
