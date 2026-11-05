#include <windows.h>
#include <malloc.h>
#include <string>
#include <fstream>
#include <cstdio>
using BeginFn = void(*)(long long,long long);
using SampleFn = void(*)(long long,long long);
using EndFn = const char*(*)(long long,long long);
static HANDLE readyEvent, completedEvent;
static volatile unsigned long long sink;
__declspec(noinline) static void BusinessWait(int amount) {
    volatile char* memory = (volatile char*)_alloca(128 + amount * 16);
    memory[0] = (char)amount;
    SetEvent(readyEvent);
    WaitForSingleObject(completedEvent, 5000);
    sink += memory[0];
}
__declspec(noinline) static void BusinessUpdate(int depth) {
    volatile char* memory = (volatile char*)_alloca(192 + depth * 16);
    memory[0] = (char)depth;
    if (depth) BusinessUpdate(depth-1); else BusinessWait(depth+3);
    sink += memory[0];
}
__declspec(noinline) static void BusinessEntry_m0123456789ABCDEF0123456789ABCDEF01234567() { BusinessUpdate(12); sink++; }
struct Args { BeginFn begin; SampleFn sample; EndFn end; std::string output; };
static DWORD WINAPI Capture(LPVOID state) {
    auto& args = *(Args*)state;
    WaitForSingleObject(readyEvent,5000);
    Sleep(10);
    args.begin(1,1000); args.sample(1,1000);
    const char* json = args.end(1,1001);
    args.output = json ? json : "{}";
    SetEvent(completedEvent);
    return 0;
}
int main(int argc, char** argv) {
    if(argc!=3)return 1;
    HMODULE dll=LoadLibraryA(argv[1]);if(!dll)return 2;
    auto reg=(int(*)())GetProcAddress(dll,"PerfNative_RegisterMainThread");if(!reg||reg()!=1)return 3;
    readyEvent=CreateEventW(nullptr,FALSE,FALSE,nullptr);completedEvent=CreateEventW(nullptr,FALSE,FALSE,nullptr);
    Args args{(BeginFn)GetProcAddress(dll,"PerfNative_BeginStall"),(SampleFn)GetProcAddress(dll,"PerfNative_RequestSample"),(EndFn)GetProcAddress(dll,"PerfNative_EndStall")};
    HANDLE worker=CreateThread(nullptr,0,Capture,&args,0,nullptr);
    BusinessEntry_m0123456789ABCDEF0123456789ABCDEF01234567();WaitForSingleObject(worker,5000);CloseHandle(worker);
    std::ofstream(argv[2])<<args.output;
    return args.output.find("stack") == std::string::npos ? 4 : 0;
}
