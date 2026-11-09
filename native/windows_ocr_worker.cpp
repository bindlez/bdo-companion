// Independent, opt-in Windows OCR worker. Images stay in anonymous pipes/RAM.
// The caller owns request deadlines and kills this process on timeout.
#define NOMINMAX
#include <windows.h>
#include <io.h>
#include <fcntl.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.Globalization.h>
#include <winrt/Windows.Graphics.Imaging.h>
#include <winrt/Windows.Media.Ocr.h>
#include <winrt/Windows.Storage.Streams.h>
#include <cstdint>
#include <cstdio>
#include <string>
#include <vector>
#pragma comment(lib,"windowsapp.lib")
using namespace winrt;
using namespace Windows::Graphics::Imaging;
using namespace Windows::Media::Ocr;
using namespace Windows::Storage::Streams;

static std::string json(hstring const& value){
 std::string result="\"";
 for(unsigned char c:to_string(value)){
  if(c=='"'||c=='\\'){result+='\\';result+=char(c);}
  else if(c=='\n')result+="\\n";
  else if(c=='\r')result+="\\r";
  else if(c=='\t')result+="\\t";
  else if(c>=32)result+=char(c);
 }
 return result+'"';
}
int main(){
 _setmode(_fileno(stdin),_O_BINARY);_setmode(_fileno(stdout),_O_BINARY);SetPriorityClass(GetCurrentProcess(),BELOW_NORMAL_PRIORITY_CLASS);
 try{
  init_apartment(apartment_type::multi_threaded);
  auto engine=OcrEngine::TryCreateFromLanguage(Windows::Globalization::Language(L"en-US"));
  if(!engine){std::puts("{\"ready\":false,\"error\":\"Windows English OCR unavailable\"}");return 3;}
  std::printf("{\"ready\":true,\"engine\":\"Windows OCR\",\"language\":%s,\"max_dimension\":%u}\n",json(engine.RecognizerLanguage().LanguageTag()).c_str(),OcrEngine::MaxImageDimension());std::fflush(stdout);
  uint32_t size=0;
  while(std::fread(&size,sizeof(size),1,stdin)==1){
   if(!size||size>2'000'000)return 4;
   std::vector<uint8_t> input(size);if(std::fread(input.data(),1,size,stdin)!=size)return 5;
   try{
    InMemoryRandomAccessStream stream;DataWriter writer(stream);writer.WriteBytes(input);writer.StoreAsync().get();writer.DetachStream();stream.Seek(0);
    auto decoder=BitmapDecoder::CreateAsync(stream).get();
    auto w=decoder.PixelWidth(),h=decoder.PixelHeight();
    if(!w||!h||uint64_t(w)*h>1'500'000||w>OcrEngine::MaxImageDimension()||h>OcrEngine::MaxImageDimension())throw hresult_invalid_argument();
    auto bitmap=decoder.GetSoftwareBitmapAsync(BitmapPixelFormat::Bgra8,BitmapAlphaMode::Ignore).get();
    auto result=engine.RecognizeAsync(bitmap).get();
    std::wstring text;
    for(auto const& row:result.Lines()){
     if(!text.empty())text+=L'\n';
     text+=row.Text().c_str();
    }
    std::printf("{\"text\":%s,\"words\":[",json(hstring(text)).c_str());bool first=true;int count=0,line=0;
    for(auto const& row:result.Lines()){
     ++line;
     for(auto const& word:row.Words()){
      if(++count>6000)throw hresult_invalid_argument();auto box=word.BoundingRect();
      if(!first)std::putchar(',');first=false;
      std::printf("{\"text\":%s,\"line\":%d,\"x\":%.2f,\"y\":%.2f,\"w\":%.2f,\"h\":%.2f}",json(word.Text()).c_str(),line,box.X,box.Y,box.Width,box.Height);
     }
    }
    std::puts("]}");
   }catch(hresult_error const&){std::puts("{\"error\":\"Windows OCR could not read this frame\"}");}
   std::fflush(stdout);
  }
  return 0;
 }catch(hresult_error const& error){std::printf("{\"ready\":false,\"error\":%s}\n",json(error.message()).c_str());return 2;}
}
