using LoadTestFactory;
using LoadTestFactory.Measurements;
using LoadTestFactory.Results;

//TestScenario[] tests = new TestScenario[]
//{
//    CreateScenario("A", 20),
//    CreateScenario("B", 7),
//    CreateScenario("C", 3),
//    CreateScenario("D", 20),
//    CreateScenario("E", 2)
//};

TestScenario[] tests = new TestScenario[]
{
    CreateScenario2("A", 73),
    CreateScenario3("B", 31),
    CreateScenario2("C", 17),
    CreateScenario("D", 73),
    CreateScenario("E", 2),
    CreateScenario2("F", 1),
    CreateScenario("G", 45),
    CreateScenario3("H", 13),
    CreateScenario("I", 44),
    CreateScenario("J", 7),
    CreateScenario("K", 3),
    CreateScenario("L", 19),
    CreateScenario("M", 31),
    CreateScenario2("test-001", 5),
    CreateScenario3("test-001", 1)
};

LoadTestResult result = await LoadTestRunner.RunAsync(16, 20, 20, tests);


static TestScenario CreateScenario(string name, int weight)
{
    return new TestScenario(name, weight, async (name) =>
        {
            await Task.Delay(0);

            MeasuredCall.AsyncRun("test-001", () => Task.Delay(1));

            await Task.Delay(0);
            //await Task.CompletedTask;
        }, false
    );
}

static TestScenario CreateScenario2(string name, int weight)
{
    return new TestScenario(name, weight, async (name) =>
    {
        await Task.Delay(0);

        MeasuredCall.AsyncRun("test-001", () => Task.Delay(1));                 

        MeasuredCall.AsyncRun("test-002", () => Task.Delay(1));

        MeasuredCall.AsyncRun("test-001", () => Task.Delay(1));

        await Task.Delay(0);

        await MeasuredCall.AsyncRun("test-003", () => Task.Delay(1));
        //await Task.CompletedTask;
    }, true
    );
}

static TestScenario CreateScenario3(string name, int weight)
{
    return new TestScenario(name, weight, async (name) =>
    {
        await Task.WhenAll(
    Task.Run(async () =>
    {
        for (int i = 0; i < 10_000; i++)
            await MeasuredCall.AsyncRun("test-001", () => Task.CompletedTask);
    }),
    Task.Run(async () =>
    {
        for (int i = 0; i < 10_000; i++)
            await MeasuredCall.AsyncRun("test-002", () => Task.CompletedTask);
    })
);
    }, true
    );
}