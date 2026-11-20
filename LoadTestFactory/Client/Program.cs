using LoadTestFactory;
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
    CreateScenario("A", 73),
    CreateScenario("B", 31),
    CreateScenario("C", 17),
    CreateScenario("D", 73),
    CreateScenario("E", 2),
    CreateScenario("F", 1),
    CreateScenario("G", 45),
    CreateScenario("H", 13),
    CreateScenario("I", 44),
    CreateScenario("J", 7),
    CreateScenario("K", 3),
    CreateScenario("L", 19),
    CreateScenario("M", 31),
    CreateScenario("N", 5),
    CreateScenario("O", 1)
};

LoadTestResult result = await LoadTestRunner.RunAsync(4, 30, 30, tests, null, true);


static TestScenario CreateScenario(string name, int weight)
{
    return new TestScenario(name, weight, async (name) =>
        {
            await Task.Delay(0);

            //await Task.CompletedTask;
        }
    );
}