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
    public static void Execute(Job job,Action<double> progress,CancellationToken ct)
    {
        var op=Catalog.Find(job.FeatureId).Operation;
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
        else if(op==Operation.PdfMerge)
        {
            using var output=new PdfDocument();foreach(var file in job.Inputs) {ct.ThrowIfCancellationRequested();using var source=PdfReader.Open(file,PdfDocumentOpenMode.Import);foreach(var page in source.Pages)output.AddPage(page);}output.Save(job.Output);
        }
        else if(op==Operation.PdfSplit)
        {
            Directory.CreateDirectory(job.Output);using var pdf=PdfReader.Open(job.Inputs[0],PdfDocumentOpenMode.Import);
            for(int i=0;i<pdf.PageCount;i++){ct.ThrowIfCancellationRequested();using var part=new PdfDocument();part.AddPage(pdf.Pages[i]);part.Save(Path.Combine(job.Output,$"page-{i+1:0000}.pdf"));progress((i+1)*100d/pdf.PageCount);}
        }
        else if(op==Operation.ImagesPdf)
        {
            using var pdf=new PdfDocument();foreach(var path in job.Inputs){ct.ThrowIfCancellationRequested();using var image=XImage.FromFile(path);var page=pdf.AddPage();page.Width=XUnit.FromPoint(image.PointWidth);page.Height=XUnit.FromPoint(image.PointHeight);using var g=XGraphics.FromPdfPage(page);g.DrawImage(image,0,0,page.Width.Point,page.Height.Point);}pdf.Save(job.Output);
        }
        else if(op==Operation.TextPdf)
        {
            GlobalFontSettings.FontResolver ??= new SystemFontResolver();using var pdf=new PdfDocument();var font=new XFont("AvaMedia",11);PdfPage? page=null;XGraphics? g=null;double y=0;
            try
            {
                foreach(var line in File.ReadAllLines(job.Inputs[0]))
                {
                    ct.ThrowIfCancellationRequested();var remaining=line;
                    do
                    {
                        if(page is null || y>page.Height.Point-45){g?.Dispose();page=pdf.AddPage();g=XGraphics.FromPdfPage(page);y=45;}
                        var count=remaining.Length;while(count>1 && g!.MeasureString(remaining[..count],font).Width>page.Width.Point-80)count--;
                        g!.DrawString(remaining[..count],font,XBrushes.Black,new XPoint(40,y));y+=17;remaining=remaining[count..];
                    }while(remaining.Length>0);
                }
                if(pdf.PageCount==0)pdf.AddPage();pdf.Save(job.Output);
            }
            finally{g?.Dispose();}
        }
        else
        {
            var pages=new List<string>();using(var pdf=UglyToad.PdfPig.PdfDocument.Open(job.Inputs[0]))foreach(var page in pdf.GetPages()){ct.ThrowIfCancellationRequested();pages.Add(ContentOrderTextExtractor.GetText(page));progress(page.Number*80d/pdf.NumberOfPages);}
            if(op==Operation.PdfText)File.WriteAllText(job.Output,string.Join("\n\f\n",pages),Encoding.UTF8);
            else if(op==Operation.PdfDocx)WriteDocx(job.Output,pages);
            else if(op==Operation.PdfXlsx)WriteXlsx(job.Output,pages);
        }
        progress(100);
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
            foreach(var path in new[]{"/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf","/System/Library/Fonts/Supplemental/Arial.ttf"})if(File.Exists(path))return File.ReadAllBytes(path);
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
