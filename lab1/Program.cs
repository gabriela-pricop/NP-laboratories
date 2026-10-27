using System;
using System.Threading;
using lab1;
using lab1.task1;

if (args.Length == 3 && args[0] == Task1A.WorkerFlag)
{
    Task1A.RunWorker(int.Parse(args[1]), args[2]);
    return;
}

if (args.Length == 3 && args[0] == Task1C.WorkerFlag)
{
    Task1C.RunWorker(int.Parse(args[1]), args[2]);
    return;
}

// IExample example = new Task1A();
// IExample example = new Task1B();
 IExample example = new Task1C();


example.Run();