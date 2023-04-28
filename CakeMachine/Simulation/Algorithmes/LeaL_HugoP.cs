using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using CakeMachine.Fabrication.ContexteProduction;
using CakeMachine.Fabrication.Elements;
using CakeMachine.Fabrication.Opérations;
using CakeMachine.Utils;

namespace CakeMachine.Simulation.Algorithmes;

internal class usineOptiTest : Algorithme
{
    /// <inheritdoc />
    public override bool SupportsAsync => true;

    /// <inheritdoc />
    public override void ConfigurerUsine(IConfigurationUsine builder)
    {
        builder.NombrePréparateurs = 10;
        builder.NombreFours = 6;
        builder.NombreEmballeuses = 15;
    }

    private class OrdreProduction
    {
        private readonly Usine _usine;
        private readonly CancellationToken _token;
        private readonly Ring<Emballage> _emballeuses;
        private readonly Ring<Cuisson> _fours;
        private readonly Ring<Préparation> _préparatrices;

        public OrdreProduction(Usine usine, CancellationToken token)
        {
            _usine = usine;
            _token = token;
            _emballeuses = new Ring<Emballage>(usine.Emballeuses);
            _fours = new Ring<Cuisson>(usine.Fours);
            _préparatrices = new Ring<Préparation>(usine.Préparateurs);
        }

        public async IAsyncEnumerable<GâteauEmballé> ProduireAsync()
        {
            while (!_token.IsCancellationRequested)
            {
                var gâteauxCuits = ProduireEtCuireParBains(_usine.OrganisationUsine.ParamètresCuisson.NombrePlaces, 6);

                var tâchesEmballage = new List<Task<GâteauEmballé>>(
                    _usine.OrganisationUsine.ParamètresCuisson.NombrePlaces * _usine.OrganisationUsine.NombreFours
                );

                await foreach (var gâteauCuit in gâteauxCuits.WithCancellation(_token))
                {
                    tâchesEmballage.Add(Task.Run(async () =>
                    {
                        var emballage = _emballeuses.Next;
                        return await emballage.EmballerAsync(gâteauCuit).ConfigureAwait(false);
                    }, _token));
                }

                var gâteauxEmballés = await Task.WhenAll(tâchesEmballage).ConfigureAwait(false);
                foreach (var gâteauEmballé in gâteauxEmballés)
                {
                    yield return gâteauEmballé;
                }
            }
        }

        private async IAsyncEnumerable<GâteauCuit> ProduireEtCuireParBains(
            ushort nombrePlacesParFour,
            ushort nombreBains)
        {
            var gâteauxCrus = PréparerConformesParBainAsync(nombrePlacesParFour, nombreBains);

            var tachesCuisson = new List<Task<GâteauCuit[]>>();
            await foreach (var bainGâteauxCrus in gâteauxCrus.WithCancellation(_token))
            {
                tachesCuisson.Add(Task.Run(async () =>
                {
                    return await _fours.Next.CuireAsync(bainGâteauxCrus);
                }));
            }

            var gâteauxCuits = new List<GâteauCuit>();
            foreach (var tacheCuisson in tachesCuisson)
            {
                var bainGâteauxCuits = await tacheCuisson;
                gâteauxCuits.AddRange(bainGâteauxCuits);
            }

            foreach (var gâteauCuit in gâteauxCuits)
            {
                yield return gâteauCuit;
            }
        }

        private async IAsyncEnumerable<GâteauCru[]> PréparerConformesParBainAsync(
     ushort gâteauxParBain, ushort bains)
        {
            var totalAPréparer = (ushort)(bains * gâteauxParBain);
            var gâteauxConformes = 0;
            var gâteauxRatés = 0;
            var gâteauxPrêts = new ConcurrentQueue<GâteauCru>();

            async Task TakeNextAndSpawnChild(uint depth)
            {
                _token.ThrowIfCancellationRequested();

                while (depth < totalAPréparer + gâteauxRatés)
                {
                    _token.ThrowIfCancellationRequested();
                    if (gâteauxConformes == totalAPréparer) return;

                    var préparatrice = _préparatrices.Next;

                    await Task.Run(async () =>
                    {
                        _token.ThrowIfCancellationRequested();

                        var gateau = await préparatrice.PréparerAsync(_usine.StockInfiniPlats.First());
                        if (gateau.EstConforme)
                        {
                            gâteauxPrêts.Enqueue(gateau);
                            Interlocked.Increment(ref gâteauxConformes);
                        }
                        else
                        {
                            Interlocked.Increment(ref gâteauxRatés);
                        }
                    });

                    depth++;
                }
            }

            var tasks = Enumerable.Range(0, Environment.ProcessorCount)
                .Select(_ => TakeNextAndSpawnChild(0))
                .ToList();

            var buffer = new List<GâteauCru>(gâteauxParBain);
            while (gâteauxConformes < totalAPréparer)
            {
                _token.ThrowIfCancellationRequested();

                GâteauCru gâteauPrêt;

                while (!gâteauxPrêts.TryDequeue(out gâteauPrêt))
                {
                    _token.ThrowIfCancellationRequested();
                    await Task.Delay(_usine.OrganisationUsine.ParamètresPréparation.TempsMin / 2, _token);
                }

                buffer.Add(gâteauPrêt);

                if (buffer.Count != gâteauxParBain) continue;

                yield return buffer.ToArray();

                buffer.Clear();
            }

            await Task.WhenAll(tasks);
        }
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<GâteauEmballé> ProduireAsync(
 Usine usine,
 [EnumeratorCancellation] CancellationToken token)
    {
        var ligne = new OrdreProduction(usine, token);
        var producerTask = Task.Run(() => ligne.ProduireAsync(), token);
        await foreach (var gâteauEmballé in producerTask.Result.WithCancellation(token))
            yield return gâteauEmballé;
    }
}