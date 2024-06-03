using System;
using System.Threading.Tasks;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Reactive.Disposables;
using System.Collections.Generic;

namespace FRecorder2
{
  public static class ObservableExtensions
  {
    public static Task<T> FirstValueFromAsync<T>(this IObservable<T> observable)
    {
      return observable.FirstAsync().ToTask();
    }

    public static IObservable<TResult> WithLatestFrom<TFirst, TSecond, TThird, TResult>(
      this IObservable<TFirst> first, IObservable<TSecond> second, IObservable<TThird> third, Func<TFirst, TSecond, TThird, TResult> resultSelector)
    {
      return first.WithLatestFrom(Observable.CombineLatest(second, third, (secondValue, thirdValue) => (secondValue, thirdValue)),
        (firstValue, x) => resultSelector(firstValue, x.secondValue, x.thirdValue));
    }

    public static IDisposable DisposeWith(this IDisposable disposable, ICollection<IDisposable> disposables)
    {
      disposables.Add(disposable);

      return disposable;
    }
  }
}